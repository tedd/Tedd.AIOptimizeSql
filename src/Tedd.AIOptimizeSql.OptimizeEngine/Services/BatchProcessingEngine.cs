using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class BatchProcessingEngine(
    IServiceScopeFactory scopeFactory,
    IAiHypothesisService hypothesisService,
    ILogger<BatchProcessingEngine> logger)
{
    public async Task ProcessBatchAsync(HypothesisBatchId batchId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting batch processing for {BatchId}", batchId);

        try
        {
            await hypothesisService.RunBatchAsync(batchId, cancellationToken);
            await CompleteBatchAsync(batchId, "All hypotheses generated");
            logger.LogInformation("Batch {BatchId} completed", batchId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Batch {BatchId} cancelled due to shutdown", batchId);
            await SetBatchStoppedAsync(batchId, "Cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Batch {BatchId} failed with error", batchId);
            await SetBatchStoppedAsync(batchId, $"Error: {ex.Message}");
        }
    }

    private async Task CompleteBatchAsync(HypothesisBatchId batchId, string message)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == batchId);
            if (batch is not null)
            {
                batch.State = HypothesisBatchState.Stopped;
                batch.EndedAt = DateTime.UtcNow;
                batch.LastMessage = message;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete batch {BatchId}", batchId);
        }
    }

    private async Task SetBatchStoppedAsync(HypothesisBatchId batchId, string message)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == batchId);
            if (batch is not null)
            {
                batch.State = HypothesisBatchState.Stopped;
                batch.EndedAt = DateTime.UtcNow;
                batch.LastMessage = message;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update batch {BatchId} state on stop", batchId);
        }
    }
}
