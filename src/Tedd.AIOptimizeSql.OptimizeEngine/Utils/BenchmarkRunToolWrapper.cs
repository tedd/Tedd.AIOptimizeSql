using System.ComponentModel;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Read-only tools that let the optimization AI inspect benchmark runs recorded for
/// the current research iteration: full IO statistics, server messages (the
/// STATISTICS IO/TIME output) and the captured actual execution plan XML. Reads
/// from the application's own database only; the target database is never touched.
/// Access is scoped to the iteration's own runs (baseline and hypothesis before/after).
/// </summary>
public sealed class BenchmarkRunToolWrapper(
    ResearchIterationId iterationId,
    IServiceScopeFactory scopeFactory,
    int maxResponseChars,
    ILogger logger)
{
    [Description("Returns the full recorded details of a benchmark run from this research iteration: median CPU/elapsed times, complete IO statistics (scan count, logical/physical/read-ahead/LOB reads), the server messages captured during the run (STATISTICS IO/TIME output), and how many actual execution plans were recorded. Valid ids are the baseline run and the before/after runs listed with previous attempts.")]
    public async Task<string> GetBenchmarkRunDetails(
        [Description("Benchmark run id, e.g. the baseline run or a previous attempt's after-run")] int benchmarkRunId)
    {
        logger.LogDebug("AI tool: GetBenchmarkRunDetails {RunId}", benchmarkRunId);

        try
        {
            var run = await LoadRunIfInIterationAsync(benchmarkRunId);
            if (run is null)
                return $"ERROR: benchmark run {benchmarkRunId} does not exist or does not belong to this research iteration.";

            var sb = new StringBuilder();
            sb.AppendLine($"# Benchmark run {benchmarkRunId}");
            sb.AppendLine();
            sb.AppendLine($"- CPU time (median across iterations): {run.TotalServerCpuTimeMs} ms");
            sb.AppendLine($"- Elapsed time (median across iterations): {run.TotalServerElapsedTimeMs} ms");
            sb.AppendLine($"- Wall-clock for the whole benchmark: {run.TotalTimeMs} ms");
            sb.AppendLine($"- Scan count: {run.TotalScanCount}");
            sb.AppendLine($"- Logical reads: {run.TotalLogicalReads}");
            sb.AppendLine($"- Physical reads: {run.TotalPhysicalReads}");
            sb.AppendLine($"- Read-ahead reads: {run.TotalReadAheadReads}");
            sb.AppendLine($"- Page server reads: {run.TotalPageServerReads} (read-ahead: {run.TotalPageServerReadAheadReads})");
            sb.AppendLine($"- LOB logical reads: {run.TotalLobLogicalReads}");
            sb.AppendLine($"- LOB physical reads: {run.TotalLobPhysicalReads}");
            sb.AppendLine($"- LOB read-ahead reads: {run.TotalLobReadAheadReads}");
            sb.AppendLine($"- LOB page server reads: {run.TotalLobPageServerReads} (read-ahead: {run.TotalLobPageServerReadAheadReads})");
            sb.AppendLine($"- Actual execution plans captured: {run.ActualPlanXml.Count}" +
                          (run.ActualPlanXml.Count > 0
                              ? $" (fetch with GetBenchmarkRunPlanXml, planIndex 0..{run.ActualPlanXml.Count - 1})"
                              : ""));

            if (!string.IsNullOrWhiteSpace(run.Messages))
            {
                sb.AppendLine();
                sb.AppendLine("## Server messages (STATISTICS IO/TIME output)");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(run.Messages.Trim());
                sb.AppendLine("```");
            }

            return Truncate(sb.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: GetBenchmarkRunDetails failed for run {RunId}", benchmarkRunId);
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Returns the actual execution plan XML captured during a benchmark run of this research iteration. Actual plans include runtime row counts, memory grant usage, spill warnings, and per-operator statistics — far richer than an estimated plan. Use GetBenchmarkRunDetails first to see how many plans the run captured.")]
    public async Task<string> GetBenchmarkRunPlanXml(
        [Description("Benchmark run id")] int benchmarkRunId,
        [Description("Zero-based plan index when the run captured several statements (default 0)")] int planIndex = 0)
    {
        logger.LogDebug("AI tool: GetBenchmarkRunPlanXml {RunId}[{Index}]", benchmarkRunId, planIndex);

        try
        {
            var run = await LoadRunIfInIterationAsync(benchmarkRunId);
            if (run is null)
                return $"ERROR: benchmark run {benchmarkRunId} does not exist or does not belong to this research iteration.";

            if (run.ActualPlanXml.Count == 0)
                return $"ERROR: benchmark run {benchmarkRunId} captured no actual execution plans.";

            if (planIndex < 0 || planIndex >= run.ActualPlanXml.Count)
                return $"ERROR: planIndex {planIndex} is out of range; run {benchmarkRunId} has {run.ActualPlanXml.Count} plan(s) (valid indexes 0..{run.ActualPlanXml.Count - 1}).";

            return Truncate(run.ActualPlanXml[planIndex]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI tool: GetBenchmarkRunPlanXml failed for run {RunId}", benchmarkRunId);
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the run only when it belongs to this iteration: its baseline run, or a
    /// before/after run of one of its hypotheses.
    /// </summary>
    private async Task<BenchmarkRun?> LoadRunIfInIterationAsync(int benchmarkRunId)
    {
        var runId = (BenchmarkRunId)benchmarkRunId;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();

        var belongsToIteration =
            await db.ResearchIterations.AsNoTracking()
                .AnyAsync(r => r.Id == iterationId && r.BaselineBenchmarkRunId == runId)
            || await db.Hypotheses.AsNoTracking()
                .AnyAsync(h => h.ResearchIterationId == iterationId
                    && (h.BenchmarkRunIdBefore == runId || h.BenchmarkRunIdAfter == runId));

        if (!belongsToIteration)
            return null;

        return await db.BenchmarkRuns.AsNoTracking().FirstOrDefaultAsync(b => b.Id == runId);
    }

    private string Truncate(string value)
    {
        var max = Math.Max(1_024, maxResponseChars);
        return value.Length <= max ? value : value[..max] + "\n… (truncated)";
    }
}
