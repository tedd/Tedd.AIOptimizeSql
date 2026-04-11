using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Polls <see cref="RunQueue"/> at a configurable interval and hands dequeued
/// batches to <see cref="BatchProcessingEngine"/> for processing.
/// </summary>
public sealed class QueueMonitorService(
    IServiceScopeFactory scopeFactory,
    BatchProcessingEngine batchEngine,
    IOptions<OptimizeEngineSettings> settings,
    ILogger<QueueMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QueueMonitorService started, polling every {Interval}s",
            settings.Value.QueuePollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batchId = await TryDequeueAsync(stoppingToken);
                if (batchId is not null)
                {
                    logger.LogInformation("Dequeued batch {BatchId}, starting processing", batchId);

                    using var scope = scopeFactory.CreateScope();
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IAIOptimizeDataAccess>();
                    await dataAccess.BeginHypothesisBatchRunAsync(batchId.Value, stoppingToken);

                    await batchEngine.ProcessBatchAsync(batchId.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during queue poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.Value.QueuePollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("QueueMonitorService stopped");
    }

    /// <summary>
    /// Attempts to atomically remove one item from the <see cref="RunQueue"/>.
    /// Returns the <see cref="HypothesisBatchId"/> if successful, or null if the
    /// queue is empty or another worker claimed the item (race condition).
    /// </summary>
    private async Task<HypothesisBatchId?> TryDequeueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();

        var item = await db.RunQueue
            .OrderBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return null;

        var batchId = item.HypothesisBatchId;
        db.RunQueue.Remove(item);

        try
        {
            await db.SaveChangesAsync(ct);
            return batchId;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Queue item for batch {BatchId} was already claimed by another worker", batchId);
            return null;
        }
    }
}
