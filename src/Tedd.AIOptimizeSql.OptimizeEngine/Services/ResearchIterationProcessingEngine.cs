using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class ResearchIterationProcessingEngine(
    IServiceScopeFactory scopeFactory,
    IAiHypothesisService hypothesisService,
    ResearchIterationLogger iterationLogger,
    ExperimentSandboxCoordinator sandboxCoordinator,
    ILogger<ResearchIterationProcessingEngine> logger)
{
    public async Task ProcessIterationAsync(ResearchIterationId iterationId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting research iteration processing for {IterationId}", iterationId);

        try
        {
            try
            {
                await iterationLogger.AppendAsync(iterationId,
                    "Iteration dequeued from run queue; processing started.",
                    "ProcessingEngine", cancellationToken);

                await hypothesisService.RunIterationAsync(
                    iterationId,
                    cancellationToken,
                    runStartedLogLine: "[QueueMonitor] Iteration dequeued from run queue; hypothesis generation started.");

                await RunExperimentPostRunSqlAsync(iterationId, cancellationToken);
                await iterationLogger.AppendAsync(iterationId,
                    "Iteration completed: all hypotheses generated and tested.",
                    "ProcessingEngine", CancellationToken.None);
                await CompleteIterationAsync(iterationId, "All hypotheses generated and tested");
                logger.LogInformation("Research iteration {IterationId} completed", iterationId);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Research iteration {IterationId} cancelled due to shutdown", iterationId);
                await iterationLogger.AppendAsync(
                    iterationId,
                    "Research iteration cancelled (host shutdown or token cancelled).",
                    "ProcessingEngine",
                    CancellationToken.None);
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
                await iterationLogger.AppendAsync(
                    iterationId,
                    $"Research iteration processing failed: {ex}",
                    "ProcessingEngine",
                    CancellationToken.None);
                await hypothesisService.AppendLogToLatestHypothesisInIterationAsync(
                    iterationId,
                    $"Research iteration processing failed: {ex}",
                    "ProcessingEngine",
                    CancellationToken.None);
                await SetIterationStoppedAsync(iterationId, $"Error: {ex.Message}");
            }
        }
        finally
        {
            // Always attempted, regardless of how the iteration ended -- a leaked clone
            // database or sandbox schema is the worst outcome here.
            await sandboxCoordinator.TeardownAsync(
                iterationId,
                msg => iterationLogger.AppendAsync(iterationId, msg, "Sandbox", CancellationToken.None).GetAwaiter().GetResult(),
                CancellationToken.None);
        }
    }

    private async Task RunExperimentPostRunSqlAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var iteration = await db.ResearchIterations
                .AsNoTracking()
                .Include(r => r.Experiment!)
                    .ThenInclude(e => e.DatabaseConnection)
                .FirstOrDefaultAsync(r => r.Id == iterationId, ct);

            if (iteration?.Experiment is null) return;
            var postRunSql = iteration.Experiment.ExperimentPostRunSql;
            if (string.IsNullOrWhiteSpace(postRunSql)) return;
            var connStr = ExperimentSandboxCoordinator.ResolveConnectionString(iteration.Experiment);
            if (string.IsNullOrWhiteSpace(connStr)) return;

            logger.LogInformation("Running ExperimentPostRunSql for iteration {IterationId}", iterationId);
            await iterationLogger.AppendAsync(iterationId,
                HypothesisTestingService.WithSql("Running ExperimentPostRunSql", postRunSql),
                "ProcessingEngine", ct);

            var executor = DatabaseExecutorFactory.Create(
                new OptimizeEngine.Models.BenchmarkConfig { DatabaseType = "MSSQL" },
                msg => logger.LogDebug("{SqlLog}", msg));

            await using var conn = await executor.OpenConnectionAsync(connStr, ct);
            executor.ExecuteNonQuery(conn, postRunSql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ExperimentPostRunSql failed for iteration {IterationId}", iterationId);
            await iterationLogger.AppendAsync(iterationId,
                $"ExperimentPostRunSql failed: {ex.Message}",
                "ProcessingEngine", CancellationToken.None);
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
                    .SetProperty(b => b.LastMessage, message)
                    .SetProperty(b => b.ModifiedAt, ended));
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
                    .SetProperty(b => b.LastMessage, message)
                    .SetProperty(b => b.ModifiedAt, ended));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update research iteration {IterationId} state on stop", iterationId);
        }
    }
}
