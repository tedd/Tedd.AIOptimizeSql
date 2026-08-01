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
using Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Implements the Apply → Benchmark → Revert cycle for a single hypothesis.
/// Also provides baseline benchmark functionality for a research iteration.
/// </summary>
public sealed class HypothesisTestingService(
    AiAgentFactory agentFactory,
    AiConversationTracker conversationTracker,
    IServiceScopeFactory scopeFactory,
    ResearchIterationLogger iterationLogger,
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

        if (dbConnection.AnalyzeOnly)
            throw new InvalidOperationException(
                "Connection is analyze-only: benchmarking clears caches and updates statistics, which is not allowed.");

        if (string.IsNullOrWhiteSpace(experiment.BenchmarkSql))
            throw new InvalidOperationException("BenchmarkSql is required for benchmarking.");

        var warmUps = settings.Value.WarmUpIterations;
        var timedRuns = settings.Value.BenchmarkIterations;
        var totalSw = Stopwatch.StartNew();

        // Live feedback: activity log rows plus a moving status line with elapsed time.
        Task Log(string message) =>
            iterationLogger.AppendAsync(iteration.Id, message, "Benchmark", CancellationToken.None);
        Task Status(string message) =>
            iterationLogger.SetMessageAsync(iteration.Id,
                $"Baseline benchmark: {message} ({FormatDuration(totalSw.Elapsed)} elapsed)", CancellationToken.None);

        var config = new BenchmarkConfig
        {
            DatabaseType = "MSSQL",
            PostClearStabilizationMs = settings.Value.PostClearStabilizationMs
        };
        var executor = DatabaseExecutorFactory.Create(config, msg => _logger.LogDebug("{SqlLog}", msg));

        await Log(WithSql(
            $"Baseline benchmark starting: {warmUps} warm-up + {timedRuns} timed runs. Each run clears caches " +
            "(CHECKPOINT; DBCC DROPCLEANBUFFERS; DBCC FREEPROCCACHE), waits for storage I/O to settle " +
            $"(+{config.PostClearStabilizationMs} ms), then executes the benchmark SQL below with SET STATISTICS TIME/IO/XML ON.",
            experiment.BenchmarkSql));

        var connectionString = ExperimentSandboxCoordinator.ResolveConnectionString(experiment);
        await using var conn = await executor.OpenConnectionAsync(connectionString, ct);

        // Run ExperimentPreRunSql if configured
        if (!string.IsNullOrWhiteSpace(experiment.ExperimentPreRunSql))
        {
            _logger.LogInformation("Running ExperimentPreRunSql");
            await Status("running ExperimentPreRunSql…");
            var preSw = Stopwatch.StartNew();
            executor.ExecuteNonQuery(conn, experiment.ExperimentPreRunSql);
            await Log(WithSql($"ExperimentPreRunSql completed in {FormatDuration(preSw.Elapsed)}.", experiment.ExperimentPreRunSql));
        }

        // Update statistics before baseline
        _logger.LogInformation("Updating statistics before baseline benchmark");
        await Status("updating statistics on all tables…");
        await Log("Updating statistics (EXEC sp_MSforeachtable 'UPDATE STATISTICS ? WITH FULLSCAN') — " +
                  "on large databases this can take several minutes with no further output.");
        var statsSw = Stopwatch.StartNew();
        executor.UpdateStatistics(conn);
        await Log($"Statistics update completed in {FormatDuration(statsSw.Elapsed)}.");

        // Warm-up
        for (var i = 0; i < warmUps; i++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Baseline warm-up iteration {I}/{Total}", i + 1, warmUps);
            await Status($"warm-up {i + 1}/{warmUps} — clearing caches…");
            executor.ClearCache(conn);
            await Status($"warm-up {i + 1}/{warmUps} — executing benchmark SQL…");
            var runSw = Stopwatch.StartNew();
            var warmTiming = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql);
            await Log(DescribeRun($"Warm-up {i + 1}/{warmUps}", runSw.Elapsed, warmTiming) + " Timing discarded.");
        }

        // Timed iterations
        var timings = new List<SqlExecutionResult>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < timedRuns; i++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Baseline benchmark iteration {I}/{Total}", i + 1, timedRuns);
            await Status($"timed run {i + 1}/{timedRuns} — clearing caches…");
            executor.ClearCache(conn);
            await Status($"timed run {i + 1}/{timedRuns} — executing benchmark SQL…");
            var runSw = Stopwatch.StartNew();
            var timing = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql);
            timings.Add(timing);
            await Log(WithOutput(DescribeRun($"Timed run {i + 1}/{timedRuns}", runSw.Elapsed, timing), timing.Messages));
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
        var linkNow = DateTime.UtcNow;
        await db.ResearchIterations
            .Where(r => r.Id == iteration.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.BaselineBenchmarkRunId, aggregated.Id)
                .SetProperty(r => r.ModifiedAt, linkNow), ct);

        // Baseline output fingerprint: every hypothesis is compared against this. A failure
        // here is a warning, not fatal -- the hash stays null and comparisons downstream
        // simply report "not verified" instead of blocking the iteration.
        if (experiment.OutputVerificationMode != OutputVerificationMode.None
            && !string.IsNullOrWhiteSpace(experiment.OutputVerificationSql))
        {
            try
            {
                await Status("computing baseline output fingerprint…");
                var baselineHash = executor.ExecuteScalar(conn, experiment.OutputVerificationSql);
                var hashNow = DateTime.UtcNow;
                await db.ResearchIterations
                    .Where(r => r.Id == iteration.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.BaselineOutputHash, baselineHash)
                        .SetProperty(r => r.ModifiedAt, hashNow), ct);
                await Log($"Baseline output fingerprint: {baselineHash}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Baseline output fingerprint failed for iteration {Id}", iteration.Id);
                await Log($"Baseline output fingerprint failed: {ex.Message}. Output verification will report 'not verified' for every hypothesis.");
            }
        }

        _logger.LogInformation("Baseline benchmark complete: CPU={Cpu}ms, Elapsed={Elapsed}ms over {Iters} iterations",
            aggregated.TotalServerCpuTimeMs, aggregated.TotalServerElapsedTimeMs, settings.Value.BenchmarkIterations);

        await Log($"Baseline benchmark completed in {FormatDuration(totalSw.Elapsed)} — " +
                  $"median server elapsed {Inv(aggregated.TotalServerElapsedTimeMs)} ms, median CPU {Inv(aggregated.TotalServerCpuTimeMs)} ms " +
                  $"over {timedRuns} timed runs.");

        return aggregated;
    }

    #endregion

    #region Hypothesis Testing

    /// <summary>
    /// Runs the full Apply → Benchmark → Revert cycle for a hypothesis.
    /// Returns true if the cycle completed successfully (including revert).
    /// Returns false if revert failed -- caller must halt the iteration.
    /// <paramref name="statusLabel"/> names the hypothesis in the iteration's live status
    /// line, e.g. "hypothesis 3/10".
    /// </summary>
    public async Task<bool> TestHypothesisAsync(
        HypothesisId hypothesisId,
        ResearchIteration iteration,
        BenchmarkRun baseline,
        Action<HypothesisId, string, string?>? appendLog,
        string? statusLabel,
        CancellationToken ct)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded.");
        var aiConnection = iteration.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded.");

        var label = string.IsNullOrWhiteSpace(statusLabel) ? "hypothesis" : statusLabel;
        var totalSw = Stopwatch.StartNew();

        // Moving status line on the iteration so the UI shows what the tester is doing right now.
        Task Status(string message) =>
            iterationLogger.SetMessageAsync(iteration.Id,
                $"Testing {label}: {message} ({FormatDuration(totalSw.Elapsed)} elapsed)", CancellationToken.None);

        if (dbConnection.AnalyzeOnly)
        {
            // Safety net: callers already skip testing for analyze-only connections.
            _logger.LogWarning("Hypothesis {Id} targets an analyze-only connection; refusing Apply/Benchmark/Revert", hypothesisId);
            appendLog?.Invoke(hypothesisId,
                "Skipped Apply/Benchmark/Revert: connection is analyze-only (production-safe). The database was not modified.",
                "TestingService");
            return true;
        }

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

        var connectionString = ExperimentSandboxCoordinator.ResolveConnectionString(experiment);
        await using var conn = await executor.OpenConnectionAsync(connectionString, ct);

        var currentOptimizeSql = hypothesis.OptimizeSql;
        var currentRevertSql = hypothesis.RevertSql ?? "";
        var optimizeRetries = 0;
        var revertRetries = 0;

        // 1. HypothesisPreRunSql
        if (!string.IsNullOrWhiteSpace(experiment.HypothesisPreRunSql))
        {
            await Status("running HypothesisPreRunSql…");
            appendLog?.Invoke(hypothesisId, "Running HypothesisPreRunSql", "TestingService");
            executor.ExecuteNonQuery(conn, experiment.HypothesisPreRunSql);
        }

        // 2. Compute baseline checksums for data integrity
        var baseTableList = DeserializeBaseTables(iteration.RegisteredBaseTables);
        Dictionary<string, (long RowCount, long? Checksum, string Summary)>? baselineChecksums = null;
        if (baseTableList.Count > 0)
        {
            await Status($"computing data checksums for {baseTableList.Count} tables…");
            var checksumSw = Stopwatch.StartNew();
            baselineChecksums = executor.ComputeDataChecksums(conn, baseTableList);
            appendLog?.Invoke(hypothesisId, $"Baseline checksums computed for {baseTableList.Count} tables in {FormatDuration(checksumSw.Elapsed)}", "TestingService");
        }

        // 3. APPLY optimize_sql with retry loop
        await UpdateHypothesisStatusAsync(hypothesisId, HypothesisState.Applying, ct);
        var optimizeSucceeded = false;

        for (var retry = 1; retry <= settings.Value.AiMaxRetries; retry++)
        {
            try
            {
                await Status($"applying optimization (attempt {retry}/{settings.Value.AiMaxRetries})…");
                appendLog?.Invoke(hypothesisId, WithSql($"Applying optimization (attempt {retry}/{settings.Value.AiMaxRetries})", currentOptimizeSql), "TestingService");
                executor.ExecuteNonQuery(conn, currentOptimizeSql);
                optimizeSucceeded = true;
                optimizeRetries = retry;
                appendLog?.Invoke(hypothesisId, WithSql("Optimization applied successfully", currentOptimizeSql), "TestingService");
                break;
            }
            catch (Exception ex)
            {
                appendLog?.Invoke(hypothesisId, WithSql($"Apply attempt {retry} failed: {ex.Message}", currentOptimizeSql), "TestingService");
                _logger.LogWarning(ex, "Apply attempt {Retry} failed for hypothesis {Id}", retry, hypothesisId);

                if (retry < settings.Value.AiMaxRetries)
                {
                    await Status($"apply attempt {retry} failed — asking AI for corrected SQL…");
                    var fixResult = await RequestAiFixAsync(
                        aiConnection, iteration, hypothesisId, currentOptimizeSql, ex.Message,
                        isRevert: false, originalOptimizeSql: null, ct);

                    if (fixResult != null)
                    {
                        if (!string.IsNullOrWhiteSpace(fixResult.Optimize_sql))
                            currentOptimizeSql = fixResult.Optimize_sql;
                        if (!string.IsNullOrWhiteSpace(fixResult.Revert_sql))
                            currentRevertSql = fixResult.Revert_sql;
                        appendLog?.Invoke(hypothesisId, WithSql("AI provided corrected SQL", currentOptimizeSql), "TestingService");
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
            await Status("verifying data integrity after apply…");
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
        await Status("updating statistics on all tables…");
        appendLog?.Invoke(hypothesisId,
            "Updating statistics (EXEC sp_MSforeachtable 'UPDATE STATISTICS ? WITH FULLSCAN') — " +
            "on large databases this can take several minutes with no further output.", "TestingService");
        var statsSw = Stopwatch.StartNew();
        executor.UpdateStatistics(conn);
        appendLog?.Invoke(hypothesisId, $"Statistics update completed in {FormatDuration(statsSw.Elapsed)}.", "TestingService");

        var timedRuns = settings.Value.BenchmarkIterations;
        var afterTimings = new List<SqlExecutionResult>();
        var benchSw = Stopwatch.StartNew();
        for (var i = 0; i < timedRuns; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Status($"benchmark run {i + 1}/{timedRuns} — clearing caches…");
            executor.ClearCache(conn);
            await Status($"benchmark run {i + 1}/{timedRuns} — executing benchmark SQL…");
            var runSw = Stopwatch.StartNew();
            var timing = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql!);
            afterTimings.Add(timing);
            appendLog?.Invoke(hypothesisId,
                WithOutput(DescribeRun($"Benchmark run {i + 1}/{timedRuns}", runSw.Elapsed, timing), timing.Messages),
                "TestingService");
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

            var afterNow = DateTime.UtcNow;
            await db.Hypotheses
                .Where(h => h.Id == hypothesisId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.BenchmarkRunIdAfter, afterBenchmark.Id)
                    .SetProperty(h => h.ModifiedAt, afterNow), ct);
            await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, hypothesisId, ct);
        }

        // 5.5. Output fingerprint, taken with the optimization still applied (before revert)
        // and compared against the iteration's baseline. A mismatch means the optimization
        // changed what the query returns -- it must not be reported as a usable improvement,
        // regardless of how much faster it measured.
        string? outputHash = null;
        bool? outputMatchesBaseline = null;
        if (experiment.OutputVerificationMode != OutputVerificationMode.None
            && !string.IsNullOrWhiteSpace(experiment.OutputVerificationSql))
        {
            await Status("verifying output against baseline…");
            try
            {
                outputHash = executor.ExecuteScalar(conn, experiment.OutputVerificationSql);
                if (iteration.BaselineOutputHash is not null)
                {
                    outputMatchesBaseline = string.Equals(outputHash, iteration.BaselineOutputHash, StringComparison.Ordinal);
                    appendLog?.Invoke(hypothesisId,
                        outputMatchesBaseline.Value
                            ? $"Output fingerprint matches baseline ({outputHash})."
                            : $"Output fingerprint DOES NOT match baseline (baseline={iteration.BaselineOutputHash}, this hypothesis={outputHash}). " +
                              "This optimization changes the query's result and cannot be treated as a valid speed-up.",
                        "TestingService");
                }
                else
                {
                    appendLog?.Invoke(hypothesisId,
                        $"Output fingerprint computed ({outputHash}) but no baseline fingerprint is available to compare against -- not verified.",
                        "TestingService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Output fingerprint failed for hypothesis {Id}", hypothesisId);
                appendLog?.Invoke(hypothesisId, $"Output fingerprint failed: {ex.Message} -- not verified.", "TestingService");
            }
        }

        // 6. REVERT with retry loop
        await UpdateHypothesisStatusAsync(hypothesisId, HypothesisState.Reverting, ct);
        var revertSucceeded = false;
        var originalOptimizeSql = currentOptimizeSql;

        for (var retry = 1; retry <= settings.Value.AiMaxRetries; retry++)
        {
            try
            {
                await Status($"reverting optimization (attempt {retry}/{settings.Value.AiMaxRetries})…");
                appendLog?.Invoke(hypothesisId, WithSql($"Reverting optimization (attempt {retry}/{settings.Value.AiMaxRetries})", currentRevertSql), "TestingService");
                executor.ExecuteNonQuery(conn, currentRevertSql);
                revertSucceeded = true;
                revertRetries = retry;
                appendLog?.Invoke(hypothesisId, WithSql("Revert applied successfully", currentRevertSql), "TestingService");
                break;
            }
            catch (Exception ex)
            {
                appendLog?.Invoke(hypothesisId, WithSql($"Revert attempt {retry} failed: {ex.Message}", currentRevertSql), "TestingService");
                _logger.LogWarning(ex, "Revert attempt {Retry} failed for hypothesis {Id}", retry, hypothesisId);

                if (retry < settings.Value.AiMaxRetries)
                {
                    await Status($"revert attempt {retry} failed — asking AI for corrected SQL…");
                    var fixResult = await RequestAiFixAsync(
                        aiConnection, iteration, hypothesisId, currentRevertSql, ex.Message,
                        isRevert: true, originalOptimizeSql: originalOptimizeSql, ct);

                    if (fixResult != null && !string.IsNullOrWhiteSpace(fixResult.Revert_sql))
                    {
                        currentRevertSql = fixResult.Revert_sql;
                        appendLog?.Invoke(hypothesisId, WithSql("AI provided corrected revert SQL", currentRevertSql), "TestingService");
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
            await Status("verifying data integrity after revert…");
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
        await Status("verifying revert — re-running benchmark SQL on cold cache…");
        executor.ClearCache(conn);
        var verifySw = Stopwatch.StartNew();
        var verifyTiming = executor.ExecuteWithTiming(conn, experiment.BenchmarkSql!);
        var verifyElapsed = verifyTiming.ExecutionElapsedTimeMs + verifyTiming.ParseAndCompileElapsedTimeMs;
        var baselineElapsed = baseline.TotalServerElapsedTimeMs;
        appendLog?.Invoke(hypothesisId,
            DescribeRun("Revert verification run", verifySw.Elapsed, verifyTiming) +
            $" Baseline server elapsed for comparison: {Inv(baselineElapsed)} ms.",
            "TestingService");
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
            optimizeRetries, revertRetries, improvementPct, outputHash, outputMatchesBaseline, ct);

        if (outputMatchesBaseline == false)
        {
            appendLog?.Invoke(hypothesisId,
                $"Hypothesis testing complete but marked FAILED: output changed (improvement would have been {improvementPct:+0.##;-0.##;0}%, but the result is not the same as the baseline).",
                "TestingService");
        }
        else
        {
            appendLog?.Invoke(hypothesisId,
                $"Hypothesis testing complete. Improvement: {improvementPct:+0.##;-0.##;0}%",
                "TestingService");
        }

        // 10. HypothesisPostRunSql
        RunPostHypothesisSql(executor, conn, experiment, appendLog, hypothesisId);

        return true;
    }

    #endregion

    #region AI Fix Requests

    private async Task<AiHypothesisResponse?> RequestAiFixAsync(
        AIConnection aiConnection,
        ResearchIteration iteration,
        HypothesisId hypothesisId,
        string failedSql, string errorMessage,
        bool isRevert, string? originalOptimizeSql,
        CancellationToken ct)
    {
        var conversation = await conversationTracker.StartAsync(new AiConversationStart
        {
            Kind = AiConversationKind.HypothesisRepair,
            AiConnection = aiConnection,
            DatabaseConnectionId = iteration.Experiment?.DatabaseConnectionId,
            Title = $"Repair {(isRevert ? "revert" : "apply")} SQL — hypothesis #{(int)(object)hypothesisId}",
            RelatedExperimentId = (int)(object)iteration.ExperimentId,
            RelatedResearchIterationId = (int)(object)iteration.Id,
            RelatedHypothesisId = (int)(object)hypothesisId,
        }, ct);

        try
        {
            var fixPrompt = HypothesisPromptBuilder.BuildFixPrompt(
                failedSql, errorMessage, isRevert, originalOptimizeSql);

            var agent = agentFactory.Create(aiConnection,
                "You are a MSSQL expert. Fix the SQL script that failed.", []);

            var result = await agent.RunAsync(fixPrompt, cancellationToken: ct);
            conversation.Record(result?.Usage);
            await conversation.CompleteAsync(CancellationToken.None);
            return AiResponseParser.ParseHypothesisResponse(result?.ToString());
        }
        catch (Exception ex)
        {
            await conversation.FailAsync(ex.Message, CancellationToken.None);
            _logger.LogWarning(ex, "AI fix request failed");
            return null;
        }
    }

    #endregion

    internal static string WithSql(string message, string? sql) =>
        string.IsNullOrWhiteSpace(sql) ? message : $"{message}\n[sql]\n{sql.Trim()}\n[/sql]";

    /// <summary>Attaches raw server output (e.g. SET STATISTICS TIME/IO messages) as a collapsible block.</summary>
    internal static string WithOutput(string message, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return message;
        var trimmed = output.Trim();
        const int maxOutputChars = 4_000;
        if (trimmed.Length > maxOutputChars)
            trimmed = trimmed[..maxOutputChars] + "\n… (truncated)";
        return $"{message}\n[output]\n{trimmed}\n[/output]";
    }

    /// <summary>One-line result summary for a single benchmark execution. Invariant culture so logs are stable.</summary>
    internal static string DescribeRun(string label, TimeSpan wallTime, SqlExecutionResult timing)
    {
        var serverElapsed = timing.ExecutionElapsedTimeMs + timing.ParseAndCompileElapsedTimeMs;
        var serverCpu = timing.ExecutionCpuTimeMs + timing.ParseAndCompileCpuTimeMs;
        return $"{label} completed in {FormatDuration(wallTime)} — server elapsed {Inv(serverElapsed)} ms, " +
               $"CPU {Inv(serverCpu)} ms, logical reads {Inv(timing.TotalLogicalReads)}, physical reads {Inv(timing.TotalPhysicalReads)}.";
    }

    /// <summary>Invariant thousands-separated number for log lines ("60,297" regardless of host culture).</summary>
    internal static string Inv(long value) =>
        value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Human-readable duration with explicit units ("8.3 s", "2 m 05 s", "1 h 02 m 03 s").</summary>
    internal static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return FormattableString.Invariant($"{(int)t.TotalHours} h {t.Minutes:00} m {t.Seconds:00} s");
        if (t.TotalMinutes >= 1)
            return FormattableString.Invariant($"{t.Minutes} m {t.Seconds:00} s");
        return t.TotalSeconds >= 10
            ? FormattableString.Invariant($"{t.TotalSeconds:0} s")
            : FormattableString.Invariant($"{t.TotalSeconds:0.0} s");
    }

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
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, state)
                .SetProperty(h => h.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    private async Task UpdateHypothesisSqlAsync(
        HypothesisId id, string optimizeSql, string revertSql, int optimizeRetries, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries)
                .SetProperty(h => h.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    /// <summary>
    /// Marks a hypothesis complete. When output verification found a mismatch, the status is
    /// <see cref="HypothesisState.Failed"/> instead of <see cref="HypothesisState.Completed"/>
    /// so it cannot be selected as <c>bestPrior</c> for later hypotheses to build on -- the
    /// measured numbers are still recorded so the user can see what was tried.
    /// </summary>
    private async Task CompleteHypothesisAsync(
        HypothesisId id, string optimizeSql, string revertSql,
        int optimizeRetries, int revertRetries, float improvementPct,
        string? outputHash, bool? outputMatchesBaseline, CancellationToken ct)
    {
        var mismatched = outputMatchesBaseline == false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, mismatched ? HypothesisState.Failed : HypothesisState.Completed)
                .SetProperty(h => h.ErrorMessage, mismatched
                    ? $"Output changed: this optimization does not return the same rows as the baseline (fingerprint mismatch). Measured improvement was {improvementPct:+0.##;-0.##;0}%, but the result is not usable."
                    : null)
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries)
                .SetProperty(h => h.RevertRetryCount, revertRetries)
                .SetProperty(h => h.ImpovementPercentage, improvementPct)
                .SetProperty(h => h.OutputHash, outputHash)
                .SetProperty(h => h.OutputMatchesBaseline, outputMatchesBaseline)
                .SetProperty(h => h.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    private async Task UpdateHypothesisFailedAsync(
        HypothesisId id, string errorMessage,
        string optimizeSql, string revertSql,
        int optimizeRetries, int revertRetries, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, HypothesisState.Failed)
                .SetProperty(h => h.ErrorMessage, errorMessage)
                .SetProperty(h => h.OptimizeSql, optimizeSql)
                .SetProperty(h => h.RevertSql, revertSql)
                .SetProperty(h => h.OptimizeRetryCount, optimizeRetries)
                .SetProperty(h => h.RevertRetryCount, revertRetries)
                .SetProperty(h => h.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    #endregion
}
