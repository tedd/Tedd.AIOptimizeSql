using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Polls <see cref="RunQueue"/> at a configurable interval and hands dequeued
/// iterations to <see cref="ResearchIterationProcessingEngine"/> for processing.
/// </summary>
public sealed class QueueMonitorService(
    IServiceScopeFactory scopeFactory,
    ResearchIterationProcessingEngine iterationEngine,
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
                var iterationId = await TryDequeueAsync(stoppingToken);
                if (iterationId is not null)
                {
                    logger.LogInformation("Dequeued research iteration {IterationId}, starting processing", iterationId);

                    using var scope = scopeFactory.CreateScope();
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IAIOptimizeDataAccess>();
                    await dataAccess.BeginResearchIterationRunAsync(iterationId.Value, stoppingToken);

                    await iterationEngine.ProcessIterationAsync(iterationId.Value, stoppingToken);
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
    /// Returns the <see cref="ResearchIterationId"/> if successful, or null if the
    /// queue is empty or another worker claimed the item (race condition).
    /// </summary>
    private async Task<ResearchIterationId?> TryDequeueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();

        var item = await db.RunQueue
            .OrderBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return null;

        var iterationId = item.ResearchIterationId;
        db.RunQueue.Remove(item);

        try
        {
            await db.SaveChangesAsync(ct);
            return iterationId;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Queue item for research iteration {IterationId} was already claimed by another worker", iterationId);
            return null;
        }
    }
}
