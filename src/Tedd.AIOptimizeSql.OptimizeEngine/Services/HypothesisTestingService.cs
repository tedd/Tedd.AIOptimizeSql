using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Implements the Apply → Benchmark → Revert cycle for a single hypothesis.
/// Also provides baseline benchmark functionality for a research iteration.
/// </summary>
public sealed class HypothesisTestingService(
    AiAgentFactory agentFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<OptimizeEngineSettings> settings,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HypothesisTestingService>();

    #region Baseline Benchmark

    /// <summary>
    /// Runs warm-up + timed benchmark iterations on the benchmark SQL
    /// to establish baseline performance for the iteration.
    /// </summary>
    public async Task<BenchmarkRun> RunBaselineBenchmarkAsync(
        ResearchIteration iteration,
        CancellationToken ct)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded.");

        if (string.IsNullOrWhiteSpace(experiment.BenchmarkSql))
            throw new InvalidOperationException("BenchmarkSql is required for benchmarking.");

        var config = new BenchmarkConfig
        {
            DatabaseType = "MSSQL",
            PostClearStabilizationMs = settings.Value.PostClearStabilizationMs
        };
        var executor = DatabaseExecutorFactory.Create(config, msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, ct);

        // Run ExperimentPreRunSql if configured
        if (!string.IsNullOrWhiteSpace(experiment.ExperimentPreRunSql))
        {
            _logger.LogInformation("Running ExperimentPreRunSql");
            executor.ExecuteNonQuery(conn, experiment.ExperimentPreRunSql);
        }

        // Update statistics before baseline
        _logger.LogInformation("Updating statistics before baseline benchmark");
        executor.UpdateStatistics(conn);

        // Warm-up
        for (var i = 0; i < settings.Value.WarmUpIterations; i++)
        {
            _logger.LogDebug("Baseline warm-up iteration {I}/{Total}", i + 1, settings.Value.WarmUpIterations);
            executor.ClearCache(conn);
            executor.ExecuteWithTiming(conn, experiment.BenchmarkSql);
        }

        // Timed iterations
        var timings = new List<SqlExecutionResult>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < settings.Value.BenchmarkIterations; i++)
        {
            _logger.LogDebug("Baseline benchmark iteration {I}/{Total}", i + 1, settings.Value.BenchmarkIterations);
            executor.ClearCache(conn);
            var timing = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql);
            timings.Add(timing);
        }
        sw.Stop();

        var aggregated = AggregateBenchmarkResults(timings, (int)sw.ElapsedMilliseconds);

        // Persist
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        db.BenchmarkRuns.Add(aggregated);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // Link to iteration
        await db.ResearchIterations
            .Where(r => r.Id == iteration.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.BaselineBenchmarkRunId, aggregated.Id), ct);

        _logger.LogInformation("Baseline benchmark complete: CPU={Cpu}ms, Elapsed={Elapsed}ms over {Iters} iterations",
            aggregated.TotalServerCpuTimeMs, aggregated.TotalServerElapsedTimeMs, settings.Value.BenchmarkIterations);

        return aggregated;
    }

    #endregion

    #region Hypothesis Testing

    /// <summary>
    /// Runs the full Apply → Benchmark → Revert cycle for a hypothesis.
    /// Returns true if the cycle completed successfully (including revert).
    /// Returns false if revert failed -- caller must halt the iteration.
    /// </summary>
    public async Task<bool> TestHypothesisAsync(
        HypothesisId hypothesisId,
        ResearchIteration iteration,
        BenchmarkRun baseline,
        Action<HypothesisId, string, string?>? appendLog,
        CancellationToken ct)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded.");
        var aiConnection = iteration.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded.");

        var hypothesis = await LoadHypothesisAsync(hypothesisId, ct);
        if (hypothesis == null || string.IsNullOrWhiteSpace(hypothesis.OptimizeSql))
        {
            _logger.LogWarning("Hypothesis {Id} has no OptimizeSql, skipping test", hypothesisId);
            return true;
        }

        var config = new BenchmarkConfig
        {
            DatabaseType = "MSSQL",
            PostClearStabilizationMs = settings.Value.PostClearStabilizationMs
        };
        var executor = DatabaseExecutorFactory.Create(config, msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, ct);

        var currentOptimizeSql = hypothesis.OptimizeSql;
        var currentRevertSql = hypothesis.RevertSql ?? "";
        var optimizeRetries = 0;
        var revertRetries = 0;

        // 1. HypothesisPreRunSql
        if (!string.IsNullOrWhiteSpace(experiment.HypothesisPreRunSql))
        {
            appendLog?.Invoke(hypothesisId, "Running HypothesisPreRunSql", "TestingService");
            executor.ExecuteNonQuery(conn, experiment.HypothesisPreRunSql);
        }

        // 2. Compute baseline checksums for data integrity
        var baseTableList = DeserializeBaseTables(iteration.RegisteredBaseTables);
        Dictionary<string, (long RowCount, long? Checksum, string Summary)>? baselineChecksums = null;
        if (baseTableList.Count > 0)
        {
            baselineChecksums = executor.ComputeDataChecksums(conn, baseTableList);
            appendLog?.Invoke(hypothesisId, $"Baseline checksums computed for {baseTableList.Count} tables", "TestingService");
        }

        // 3. APPLY optimize_sql with retry loop
        await UpdateHypothesisStatusAsync(hypothesisId, HypothesisState.Applying, ct);
        var optimizeSucceeded = false;

        for (var retry = 1; retry <= settings.Value.AiMaxRetries; retry++)
        {
            try
            {
                appendLog?.Invoke(hypothesisId, $"Applying optimization (attempt {retry}/{settings.Value.AiMaxRetries})", "TestingService");
                executor.ExecuteNonQuery(conn, currentOptimizeSql);
                optimizeSucceeded = true;
                optimizeRetries = retry;
                appendLog?.Invoke(hypothesisId, "Optimization applied successfully", "TestingService");
                break;
            }
            catch (Exception ex)
            {
                appendLog?.Invoke(hypothesisId, $"Apply attempt {retry} failed: {ex.Message}", "TestingService");
                _logger.LogWarning(ex, "Apply attempt {Retry} failed for hypothesis {Id}", retry, hypothesisId);

                if (retry < settings.Value.AiMaxRetries)
                {
                    var fixResult = await RequestAiFixAsync(
                        aiConnection, currentOptimizeSql, ex.Message,
                        isRevert: false, originalOptimizeSql: null, ct);

                    if (fixResult != null)
                    {
                        if (!string.IsNullOrWhiteSpace(fixResult.Optimize_sql))
                            currentOptimizeSql = fixResult.Optimize_sql;
                        if (!string.IsNullOrWhiteSpace(fixResult.Revert_sql))
                            currentRevertSql = fixResult.Revert_sql;
                        appendLog?.Invoke(hypothesisId, "AI provided corrected SQL", "TestingService");
                    }
                }
            }
        }

        if (!optimizeSucceeded)
        {
            await UpdateHypothesisFailedAsync(hypothesisId, "Optimization failed after all retries",
                currentOptimizeSql, currentRevertSql, optimizeRetries, 0, ct);
            RunPostHypothesisSql(executor, conn, experiment, appendLog, hypothesisId);
            return true;
        }

        // Update stored SQL with any corrections
        await UpdateHypothesisSqlAsync(hypothesisId, currentOptimizeSql, currentRevertSql, optimizeRetries, ct);

        // 4. Data integrity check after apply
        if (baselineChecksums != null)
        {
            var afterApplyChecksums = executor.ComputeDataChecksums(conn, baseTableList);
            var integrityIssues = CompareChecksums(baselineChecksums, afterApplyChecksums);
            if (integrityIssues.Count > 0)
            {
                foreach (var issue in integrityIssues)
                    appendLog?.Invoke(hypothesisId, $"Data integrity warning after apply: {issue}", "TestingService");
            }
            else
            {
                appendLog?.Invoke(hypothesisId, "Data integrity check passed after apply", "TestingService");
            }
        }

        // 5. Update statistics, then benchmark
        await UpdateHypothesisStatusAsync(hypothesisId, HypothesisState.Benchmarking, ct);
        executor.UpdateStatistics(conn);

        var afterTimings = new List<SqlExecutionResult>();
        var benchSw = Stopwatch.StartNew();
        for (var i = 0; i < settings.Value.BenchmarkIterations; i++)
        {
            appendLog?.Invoke(hypothesisId, $"Benchmark iteration {i + 1}/{settings.Value.BenchmarkIterations}", "TestingService");
            executor.ClearCache(conn);
            afterTimings.Add(executor.ExecuteWithTiming(conn, experiment.BenchmarkSql!));
        }
        benchSw.Stop();

        var afterBenchmark = AggregateBenchmarkResults(afterTimings, (int)benchSw.ElapsedMilliseconds);

        // Persist after benchmark
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            db.BenchmarkRuns.Add(afterBenchmark);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            await db.Hypotheses
                .Where(h => h.Id == hypothesisId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.BenchmarkRunIdAfter, afterBenchmark.Id), ct);
        }

        // 6. REVERT with retry loop
        await UpdateHypothesisStatusAsync(hypothesisId, HypothesisState.Reverting, ct);
        var revertSucceeded = false;
        var originalOptimizeSql = currentOptimizeSql;

        for (var retry = 1; retry <= settings.Value.AiMaxRetries; retry++)
        {
            try
            {
                appendLog?.Invoke(hypothesisId, $"Reverting optimization (attempt {retry}/{settings.Value.AiMaxRetries})", "TestingService");
                executor.ExecuteNonQuery(conn, currentRevertSql);
                revertSucceeded = true;
                revertRetries = retry;
                appendLog?.Invoke(hypothesisId, "Revert applied successfully", "TestingService");
                break;
            }
            catch (Exception ex)
            {
                appendLog?.Invoke(hypothesisId, $"Revert attempt {retry} failed: {ex.Message}", "TestingService");
                _logger.LogWarning(ex, "Revert attempt {Retry} failed for hypothesis {Id}", retry, hypothesisId);

                if (retry < settings.Value.AiMaxRetries)
                {
                    var fixResult = await RequestAiFixAsync(
                        aiConnection, currentRevertSql, ex.Message,
                        isRevert: true, originalOptimizeSql: originalOptimizeSql, ct);

                    if (fixResult != null && !string.IsNullOrWhiteSpace(fixResult.Revert_sql))
                    {
                        currentRevertSql = fixResult.Revert_sql;
                        appendLog?.Invoke(hypothesisId, "AI provided corrected revert SQL", "TestingService");
                    }
                }
            }
        }

        if (!revertSucceeded)
        {
            _logger.LogError("CRITICAL: Revert failed after all retries for hypothesis {Id}. Halting iteration.", hypothesisId);
            appendLog?.Invoke(hypothesisId, "CRITICAL: Revert failed after all retries. Iteration will be halted.", "TestingService");
            await UpdateHypothesisFailedAsync(hypothesisId, "Revert failed after all retries - database may be in modified state",
                currentOptimizeSql, currentRevertSql, optimizeRetries, revertRetries, ct);
            return false;
        }

        // 7. Data integrity check after revert
        if (baselineChecksums != null)
        {
            var afterRevertChecksums = executor.ComputeDataChecksums(conn, baseTableList);
            var integrityIssues = CompareChecksums(baselineChecksums, afterRevertChecksums);
            if (integrityIssues.Count > 0)
            {
                foreach (var issue in integrityIssues)
                    appendLog?.Invoke(hypothesisId, $"Data integrity warning after revert: {issue}", "TestingService");
            }
            else
            {
                appendLog?.Invoke(hypothesisId, "Data integrity check passed after revert", "TestingService");
            }
        }

        // 8. Verify revert via timing comparison
        executor.ClearCache(conn);
        var verifyTiming = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql!);
        var verifyElapsed = verifyTiming.ExecutionElapsedTimeMs + verifyTiming.ParseAndCompileElapsedTimeMs;
        var baselineElapsed = baseline.TotalServerElapsedTimeMs;
        if (baselineElapsed > 0 && verifyElapsed < baselineElapsed * 0.5)
        {
            appendLog?.Invoke(hypothesisId,
                $"Warning: Post-revert timing ({verifyElapsed}ms) is significantly faster than baseline ({baselineElapsed}ms) - revert may be incomplete",
                "TestingService");
        }

        // 9. Compute improvement %
        var improvementPct = baselineElapsed > 0
            ? (float)((1.0 - (double)afterBenchmark.TotalServerElapsedTimeMs / baselineElapsed) * 100.0)
            : 0f;

        await CompleteHypothesisAsync(hypothesisId, currentOptimizeSql, currentRevertSql,
            optimizeRetries, revertRetries, improvementPct, ct);

        appendLog?.Invoke(hypothesisId,
            $"Hypothesis testing complete. Improvement: {improvementPct:+0.##;-0.##;0}%",
            "TestingService");

        // 10. HypothesisPostRunSql
        RunPostHypothesisSql(executor, conn, experiment, appendLog, hypothesisId);

        return true;
    }

    #endregion

    #region AI Fix Requests

    private async Task<AiHypothesisResponse?> RequestAiFixAsync(
        AIConnection aiConnection,
        string failedSql, string errorMessage,
        bool isRevert, string? originalOptimizeSql,
        CancellationToken ct)
    {
        try
        {
            var fixPrompt = HypothesisPromptBuilder.BuildFixPrompt(
                failedSql, errorMessage, isRevert, originalOptimizeSql);

            var agent = agentFactory.Create(aiConnection,
                "You are a MSSQL expert. Fix the SQL script that failed.", []);

            var result = await agent.RunAsync(fixPrompt, cancellationToken: ct);
            return AiResponseParser.ParseHypothesisResponse(result?.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI fix request failed");
            return null;
        }
    }

    #endregion

    #region Helpers

    private static BenchmarkRun AggregateBenchmarkResults(List<SqlExecutionResult> timings, int totalTimeMs)
    {
        if (timings.Count == 0)
            return new BenchmarkRun { TotalTimeMs = totalTimeMs, TotalServerCpuTimeMs = 0, TotalServerElapsedTimeMs = 0 };

        // Use median values for the aggregate
        var cpuTimes = timings.Select(t => t.ExecutionCpuTimeMs + t.ParseAndCompileCpuTimeMs).OrderBy(x => x).ToList();
        var elapsedTimes = timings.Select(t => t.ExecutionElapsedTimeMs + t.ParseAndCompileElapsedTimeMs).OrderBy(x => x).ToList();
        var mid = cpuTimes.Count / 2;
        var medianCpu = cpuTimes.Count % 2 == 0 ? (cpuTimes[mid - 1] + cpuTimes[mid]) / 2 : cpuTimes[mid];
        var medianElapsed = elapsedTimes.Count % 2 == 0 ? (elapsedTimes[mid - 1] + elapsedTimes[mid]) / 2 : elapsedTimes[mid];

        // Use last run's I/O and plans as representative
        var last = timings[^1];

        return new BenchmarkRun
        {
            TotalTimeMs = totalTimeMs,
            TotalServerCpuTimeMs = medianCpu,
            TotalServerElapsedTimeMs = medianElapsed,
            TotalScanCount = last.TotalScanCount,
            TotalLogicalReads = last.TotalLogicalReads,
            TotalPhysicalReads = last.TotalPhysicalReads,
            TotalPageServerReads = last.TotalPageServerReads,
            TotalReadAheadReads = last.TotalReadAheadReads,
            TotalPageServerReadAheadReads = last.TotalPageServerReadAheadReads,
            TotalLobLogicalReads = last.TotalLobLogicalReads,
            TotalLobPhysicalReads = last.TotalLobPhysicalReads,
            TotalLobPageServerReads = last.TotalLobPageServerReads,
            TotalLobReadAheadReads = last.TotalLobReadAheadReads,
            TotalLobPageServerReadAheadReads = last.TotalLobPageServerReadAheadReads,
            ActualPlanXml = new List<string>(last.ActualPlanXml),
            Messages = last.Messages,
        };
    }

    private static List<(string Schema, string Table)> DeserializeBaseTables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var items = JsonSerializer.Deserialize<List<BaseTableEntry>>(json);
            return items?.Select(e => (e.Schema ?? "dbo", e.Table ?? "")).Where(e => e.Item2.Length > 0).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class BaseTableEntry
    {
        public string? Schema { get; set; }
        public string? Table { get; set; }
    }

    private static List<string> CompareChecksums(
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> baseline,
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> current)
    {
        var issues = new List<string>();
        foreach (var (key, baselineVal) in baseline)
        {
            if (!current.TryGetValue(key, out var currentVal))
            {
                issues.Add($"{key}: table not found after operation");
                continue;
            }

            if (baselineVal.RowCount != currentVal.RowCount)
                issues.Add($"{key}: row count changed from {baselineVal.RowCount} to {currentVal.RowCount}");

            if (baselineVal.Checksum.HasValue && currentVal.Checksum.HasValue
                && baselineVal.Checksum != currentVal.Checksum)
                issues.Add($"{key}: checksum mismatch (baseline={baselineVal.Checksum}, current={currentVal.Checksum})");
        }
        return issues;
    }

    private static void RunPostHypothesisSql(
        IDatabaseExecutor executor, DbConnection conn, Experiment experiment,
        Action<HypothesisId, string, string?>? appendLog, HypothesisId hypothesisId)
    {
        if (!string.IsNullOrWhiteSpace(experiment.HypothesisPostRunSql))
        {
            appendLog?.Invoke(hypothesisId, "Running HypothesisPostRunSql", "TestingService");
            try { executor.ExecuteNonQuery(conn, experiment.HypothesisPostRunSql); }
            catch (Exception ex) { appendLog?.Invoke(hypothesisId, $"HypothesisPostRunSql failed: {ex.Message}", "TestingService"); }
        }
    }

    #endregion

    #region Database Updates

    private async Task<Hypothesis?> LoadHypothesisAsync(HypothesisId id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
    }

    private async Task UpdateHypothesisStatusAsync(HypothesisId id, HypothesisState state, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, state), ct);
    }

    private async Task UpdateHypothesisSqlAsync(
        HypothesisId id, string optimizeSql, string revertSql, int optimizeRetries, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries), ct);
    }

    private async Task CompleteHypothesisAsync(
        HypothesisId id, string optimizeSql, string revertSql,
        int optimizeRetries, int revertRetries, float improvementPct, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, HypothesisState.Completed)
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries)
                .SetProperty(h => h.RevertRetryCount, revertRetries)
                .SetProperty(h => h.ImpovementPercentage, improvementPct), ct);
    }

    private async Task UpdateHypothesisFailedAsync(
        HypothesisId id, string errorMessage,
        string optimizeSql, string revertSql,
        int optimizeRetries, int revertRetries, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, HypothesisState.Failed)
                .SetProperty(h => h.ErrorMessage, errorMessage)
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries)
                .SetProperty(h => h.RevertRetryCount, revertRetries), ct);
    }

    #endregion
}
