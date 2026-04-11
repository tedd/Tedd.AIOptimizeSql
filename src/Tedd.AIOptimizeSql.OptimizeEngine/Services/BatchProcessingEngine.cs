using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class BatchProcessingEngine(
    IServiceScopeFactory scopeFactory,
    IAiHypothesisService hypothesisService,
    IOptions<OptimizeEngineSettings> settings,
    ILogger<BatchProcessingEngine> logger)
{
    public async Task ProcessBatchAsync(HypothesisBatchId batchId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting batch processing for {BatchId}", batchId);

        var batch = await LoadBatchAsync(batchId, cancellationToken);
        if (batch is null)
        {
            logger.LogWarning("Batch {BatchId} not found, skipping", batchId);
            return;
        }

        try
        {
            var hypothesesCreated = batch.Hypotheses.Count;

            while (hypothesesCreated < batch.MaxNumberOfHypotheses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentState = await CheckBatchStateAsync(batchId, cancellationToken);
                if (currentState != HypothesisBatchState.Running)
                {
                    logger.LogInformation("Batch {BatchId} state changed to {State}, stopping", batchId, currentState);
                    return;
                }

                await UpdateBatchMessageAsync(batchId,
                    $"Generating hypothesis {hypothesesCreated + 1} of {batch.MaxNumberOfHypotheses}",
                    cancellationToken);

                var priorHypotheses = await GetPriorHypothesesAsync(batchId, cancellationToken);

                // Reload batch with full navigation for the AI service
                batch = await LoadBatchAsync(batchId, cancellationToken)
                    ?? throw new InvalidOperationException($"Batch {batchId} disappeared during processing.");

                var hypothesis = await hypothesisService.GenerateHypothesisAsync(batch, priorHypotheses, cancellationToken);
                await SaveHypothesisAsync(hypothesis, cancellationToken);
                hypothesesCreated++;

                logger.LogInformation("Hypothesis #{Number} created for batch {BatchId}", hypothesesCreated, batchId);

                // Brief check interval before next hypothesis
                await WaitWithStateCheckAsync(batchId, settings.Value.BatchStateCheckIntervalSeconds, cancellationToken);
            }

            await CompleteBatchAsync(batchId, "All hypotheses generated", cancellationToken);
            logger.LogInformation("Batch {BatchId} completed ({Count} hypotheses)", batchId, hypothesesCreated);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Batch {BatchId} cancelled due to shutdown", batchId);
            await StopBatchAsync(batchId, "Cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Batch {BatchId} failed with error", batchId);
            await StopBatchAsync(batchId, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for the configured interval while periodically checking for state changes.
    /// Returns early if state is no longer Running.
    /// </summary>
    private async Task WaitWithStateCheckAsync(HypothesisBatchId batchId, int totalSeconds, CancellationToken cancellationToken)
    {
        var elapsed = 0;
        while (elapsed < totalSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waitSeconds = Math.Min(totalSeconds - elapsed, totalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            elapsed += waitSeconds;

            var state = await CheckBatchStateAsync(batchId, cancellationToken);
            if (state != HypothesisBatchState.Running)
                return;
        }
    }

    private async Task<HypothesisBatch?> LoadBatchAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.HypothesisBatches
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.DatabaseConnection)
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.AIConnection)
            .Include(b => b.AIConnection)
            .Include(b => b.Hypotheses)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
    }

    private async Task<HypothesisBatchState> CheckBatchStateAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var state = await db.HypothesisBatches
            .Where(b => b.Id == batchId)
            .Select(b => b.State)
            .FirstOrDefaultAsync(ct);
        return state;
    }

    private async Task<IReadOnlyList<Hypothesis>> GetPriorHypothesesAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses
            .AsNoTracking()
            .Where(h => h.HypothesisBatchId == batchId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
    }

    private async Task SaveHypothesisAsync(Hypothesis hypothesis, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        db.Hypotheses.Add(hypothesis);
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateBatchMessageAsync(HypothesisBatchId batchId, string message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is not null)
        {
            batch.LastMessage = message;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task CompleteBatchAsync(HypothesisBatchId batchId, string message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is not null)
        {
            batch.State = HypothesisBatchState.Stopped;
            batch.EndedAt = DateTime.UtcNow;
            batch.LastMessage = message;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task StopBatchAsync(HypothesisBatchId batchId, string message)
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
