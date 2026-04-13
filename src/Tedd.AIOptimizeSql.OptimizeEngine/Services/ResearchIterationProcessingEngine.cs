using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class ResearchIterationProcessingEngine(
    IServiceScopeFactory scopeFactory,
    IAiHypothesisService hypothesisService,
    ILogger<ResearchIterationProcessingEngine> logger)
{
    public async Task ProcessIterationAsync(ResearchIterationId iterationId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting research iteration processing for {IterationId}", iterationId);

        try
        {
            await hypothesisService.RunIterationAsync(
                iterationId,
                cancellationToken,
                runStartedLogLine: "[QueueMonitor] Iteration dequeued from run queue; hypothesis generation started.");
            await CompleteIterationAsync(iterationId, "All hypotheses generated");
            logger.LogInformation("Research iteration {IterationId} completed", iterationId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Research iteration {IterationId} cancelled due to shutdown", iterationId);
            await hypothesisService.AppendLogToLatestHypothesisInIterationAsync(
                iterationId,
                "Research iteration cancelled (host shutdown or token cancelled).",
                "ProcessingEngine",
                CancellationToken.None);
            await SetIterationStoppedAsync(iterationId, "Cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research iteration {IterationId} failed with error", iterationId);
            await hypothesisService.AppendLogToLatestHypothesisInIterationAsync(
                iterationId,
                $"Research iteration processing failed: {ex}",
                "ProcessingEngine",
                CancellationToken.None);
            await SetIterationStoppedAsync(iterationId, $"Error: {ex.Message}");
        }
    }

    private async Task CompleteIterationAsync(ResearchIterationId iterationId, string message)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var ended = DateTime.UtcNow;
            await db.ResearchIterations
                .Where(b => b.Id == iterationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, ResearchIterationState.Stopped)
                    .SetProperty(b => b.EndedAt, ended)
                    .SetProperty(b => b.LastMessage, message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete research iteration {IterationId}", iterationId);
        }
    }

    private async Task SetIterationStoppedAsync(ResearchIterationId iterationId, string message)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var ended = DateTime.UtcNow;
            await db.ResearchIterations
                .Where(b => b.Id == iterationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, ResearchIterationState.Stopped)
                    .SetProperty(b => b.EndedAt, ended)
                    .SetProperty(b => b.LastMessage, message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update research iteration {IterationId} state on stop", iterationId);
        }
    }
}
