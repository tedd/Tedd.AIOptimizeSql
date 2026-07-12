using System.Text;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

internal static class HypothesisPromptBuilder
{
    /// <summary>Per-field cap for finding content embedded in the prompt, so one verbose
    /// finding cannot crowd out the rest of the instructions.</summary>
    private const int MaxFindingFieldChars = 4_000;

    public static string BuildInstructions(
        Experiment experiment,
        ResearchIteration iteration,
        IReadOnlyList<Hypothesis> priorHypotheses,
        string? schemaDiscoveryMarkdown = null,
        string? baselinePerformanceSummary = null,
        bool analyzeOnly = false,
        int maxAgentRuns = 20,
        IReadOnlyList<AnalysisFinding>? relatedFindings = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a MSSQL performance optimization expert.");
        sb.AppendLine("Your goal is to propose a single, concrete optimisation for the SQL workload described below.");
        if (analyzeOnly)
        {
            sb.AppendLine("You have access to READ-ONLY tools: SQL queries (SELECT/DMV), estimated execution plans, performance metrics, and schema metadata on the target database.");
        }
        else
        {
            sb.AppendLine("You have access to tools that let you execute SQL queries, run DDL/DML statements, inspect execution plans, and query schema metadata on the target database.");
        }
        sb.AppendLine();

        if (analyzeOnly)
        {
            sb.AppendLine("## CRITICAL: analyze-only mode (production-safe)");
            sb.AppendLine();
            sb.AppendLine("The target database is marked analyze-only (it may be production). Nothing may be modified:");
            sb.AppendLine("- Modifying statements are blocked by a safety guard; do not attempt DDL/DML.");
            sb.AppendLine("- Your proposed optimize_sql / revert_sql will NOT be executed or benchmarked here. They are stored for the user to review and to test on a non-production copy.");
            sb.AppendLine("- Base your reasoning on estimated execution plans (GetExecutionPlan compiles without executing) and DMV evidence instead of measured runs.");
            sb.AppendLine();
        }

        sb.AppendLine(AgentTaskPromptSection.Build(maxAgentRuns));
        sb.AppendLine();

        // Constraints
        sb.AppendLine("## Important Constraints");
        sb.AppendLine();
        sb.AppendLine("- Propose exactly ONE optimisation per response.");
        sb.AppendLine("- The optimisation MUST be fully revertible. Your revert_sql must completely undo optimize_sql.");
        sb.AppendLine("- Do NOT execute destructive operations that could corrupt or lose data.");
        sb.AppendLine("- Do NOT modify data rows (INSERT/UPDATE/DELETE on user data). Schema and index changes only.");
        sb.AppendLine("- Use IF EXISTS / IF NOT EXISTS checks where possible for idempotency.");
        sb.AppendLine("- In two-part names like [Prefix].[ObjectName], assume Prefix is the SCHEMA name, not the database name.");
        sb.AppendLine("- Handle constraint conflicts (temporarily drop/re-add if needed).");
        sb.AppendLine("- Focus on SERVER-SIDE optimizations. We measure execution performance, not client/network transfer time.");
        sb.AppendLine();

        // Optimization categories
        sb.AppendLine("## Allowed Optimization Categories");
        sb.AppendLine();
        sb.AppendLine("1. Query shape and relational rewrite — predicate/join/aggregation rewrite, sargability, projection minimization, RBAR elimination, window-function strategy, existence/semi-join rewrites, OR/UNION ALL tradeoffs");
        sb.AppendLine("2. Access path and indexing — covering/filtered/included-column indexes, seek vs scan, key column order, index intersection");
        sb.AppendLine("3. Cardinality estimation and statistics — histogram quality, multi-column correlation, ascending key issues, parameter sensitivity, filtered stats, CE model version effects");
        sb.AppendLine("4. Plan selection, stability, and parameter sensitivity — plan guides, forced plans, query store hints, OPTION(RECOMPILE) tradeoffs, parameter sniffing, OPTIMIZE FOR");
        sb.AppendLine("5. Memory grants, spills, and intermediate-result management — grant sizing, spills to tempdb, sort/hash pressure, grant feedback");
        sb.AppendLine("6. Parallelism and CPU execution strategy — DOP selection, exchange operators, skew, serial zones, batch mode eligibility, scalar UDF inlining");
        sb.AppendLine("7. Physical storage, compression, and table layout — page/row compression, heap vs clustered, fill factor, partitioning");
        sb.AppendLine("8. Columnstore and analytical execution strategy — columnstore indexes, batch mode, segment elimination, rowgroup quality");
        sb.AppendLine("9. Materialization, caching, and precomputation — indexed views, computed columns, pre-aggregation tables");
        sb.AppendLine("10. Concurrency, locking, isolation, and versioning — lock granularity, isolation levels, RCSI/snapshot, blocking reduction");
        sb.AppendLine("11. Tempdb and transient-object pressure — temp tables, table variables, worktables/workfiles, spill behavior");
        sb.AppendLine("12. Encapsulating object design — proc/view/function restructuring, inline TVF vs multi-statement TVF, trigger optimization");
        sb.AppendLine("13. Maintenance and background data health — index fragmentation, statistics freshness, ghost cleanup");
        sb.AppendLine("14. Workload governance and resource contention — Resource Governor, memory pressure, admission control");
        sb.AppendLine("15. Application access pattern and boundary effects — parameter type mismatches, SET options affecting plan reuse, cross-database references");
        sb.AppendLine("16. Observability, regression detection, and validation — wait profile, actual vs estimated row divergence, query store evidence");
        sb.AppendLine();

        // Benchmark SQL
        if (!string.IsNullOrWhiteSpace(experiment.BenchmarkSql))
        {
            sb.AppendLine("## Benchmark SQL (the query to optimise)");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(experiment.BenchmarkSql);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Experiment description (what the experiment tests and why)
        if (!string.IsNullOrWhiteSpace(experiment.Description))
        {
            sb.AppendLine("## Experiment description (what this experiment tests and why)");
            sb.AppendLine();
            sb.AppendLine(experiment.Description);
            sb.AppendLine();
        }

        // Experiment instructions
        if (!string.IsNullOrWhiteSpace(experiment.Instructions))
        {
            sb.AppendLine("## Experiment-specific instructions");
            sb.AppendLine();
            sb.AppendLine(experiment.Instructions);
            sb.AppendLine();
        }

        // Analysis findings this experiment was created to verify
        if (relatedFindings is { Count: > 0 })
        {
            sb.AppendLine("## Related analysis findings");
            sb.AppendLine();
            sb.AppendLine("This experiment was proposed by a database analysis to verify the finding(s) below. Use them as your starting point, but verify their evidence against the live database before relying on it.");
            sb.AppendLine();

            foreach (var f in relatedFindings)
            {
                sb.AppendLine($"### Finding #{(int)f.Id} [{f.Severity}/{f.Category}]: {f.Title}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(f.ObjectName))
                {
                    var obj = string.IsNullOrWhiteSpace(f.ObjectSchema) ? f.ObjectName : $"{f.ObjectSchema}.{f.ObjectName}";
                    sb.AppendLine($"- **Affected object**: {obj}");
                }
                if (f.ImpactScore > 0)
                    sb.AppendLine($"- **Impact score**: {f.ImpactScore:0.##}");

                if (!string.IsNullOrWhiteSpace(f.Description))
                {
                    sb.AppendLine();
                    sb.AppendLine(Clip(f.Description));
                }
                if (!string.IsNullOrWhiteSpace(f.Evidence))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Evidence:**");
                    sb.AppendLine();
                    sb.AppendLine(Clip(f.Evidence));
                }
                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Recommendation:**");
                    sb.AppendLine();
                    sb.AppendLine(Clip(f.Recommendation));
                }
                if (!string.IsNullOrWhiteSpace(f.RecommendationSql))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Recommended SQL (candidate only, not yet applied):**");
                    sb.AppendLine();
                    sb.AppendLine("```sql");
                    sb.AppendLine(Clip(f.RecommendationSql));
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
        }

        // Schema discovery
        if (!string.IsNullOrWhiteSpace(schemaDiscoveryMarkdown))
        {
            sb.AppendLine("## Schema Information (discovered from database catalog)");
            sb.AppendLine();
            sb.AppendLine(schemaDiscoveryMarkdown);
            sb.AppendLine();
        }

        // Baseline performance
        if (!string.IsNullOrWhiteSpace(baselinePerformanceSummary))
        {
            sb.AppendLine("## Baseline Performance (before any optimisation)");
            sb.AppendLine();
            sb.AppendLine(baselinePerformanceSummary);
            sb.AppendLine();
        }

        // Response format
        sb.AppendLine("## Required Response Format");
        sb.AppendLine();
        sb.AppendLine("You MUST respond with a JSON object containing exactly these fields:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"description\": \"Clear description of the proposed change and reasoning\",");
        sb.AppendLine("  \"optimize_sql\": \"T-SQL script to apply the optimisation\",");
        sb.AppendLine("  \"revert_sql\": \"T-SQL script that completely undoes the optimisation\"");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Do not include any text outside the JSON object. Do not wrap in markdown code fences.");
        sb.AppendLine();

        return sb.ToString();
    }

    public static string BuildPrompt(
        ResearchIteration iteration,
        IReadOnlyList<Hypothesis> priorHypotheses)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(iteration.Hints))
        {
            sb.AppendLine("## Additional hints for this research iteration");
            sb.AppendLine();
            sb.AppendLine(iteration.Hints);
            sb.AppendLine();
        }

        if (priorHypotheses.Count > 0)
        {
            sb.AppendLine("## Previous attempts in this iteration");
            sb.AppendLine();
            sb.AppendLine("Analyse the previous attempts carefully. Learn from which were GOOD, BAD, FAILED, or had revert/integrity risks. Do NOT repeat materially the same approach if it already failed or regressed.");
            sb.AppendLine();
            sb.AppendLine("Where a benchmark run id is shown, call GetBenchmarkRunDetails(id) for the full IO statistics and server messages (STATISTICS IO/TIME), and GetBenchmarkRunPlanXml(id, planIndex) for the actual execution plan XML of that run.");
            sb.AppendLine();

            for (var i = 0; i < priorHypotheses.Count; i++)
            {
                var h = priorHypotheses[i];
                var outcome = ClassifyOutcome(h);

                sb.AppendLine($"### Attempt {i + 1}: {outcome}");
                sb.AppendLine();
                sb.AppendLine($"- **Improvement**: {h.ImpovementPercentage:+0.##;-0.##;0}%");
                sb.AppendLine($"- **Status**: {h.Status}");

                if (!string.IsNullOrWhiteSpace(h.Description))
                    sb.AppendLine($"- **Description**: {h.Description}");

                if (h.BenchmarkRunBefore != null)
                    sb.AppendLine($"- **Baseline (before)**: CPU {h.BenchmarkRunBefore.TotalServerCpuTimeMs}ms, Elapsed {h.BenchmarkRunBefore.TotalServerElapsedTimeMs}ms, Logical Reads {h.BenchmarkRunBefore.TotalLogicalReads}, Physical Reads {h.BenchmarkRunBefore.TotalPhysicalReads} (benchmark run {(int)h.BenchmarkRunBefore.Id})");

                if (h.BenchmarkRunAfter != null)
                    sb.AppendLine($"- **Result (after)**: CPU {h.BenchmarkRunAfter.TotalServerCpuTimeMs}ms, Elapsed {h.BenchmarkRunAfter.TotalServerElapsedTimeMs}ms, Logical Reads {h.BenchmarkRunAfter.TotalLogicalReads}, Physical Reads {h.BenchmarkRunAfter.TotalPhysicalReads} (benchmark run {(int)h.BenchmarkRunAfter.Id})");

                if (!string.IsNullOrWhiteSpace(h.OptimizeSql))
                {
                    sb.AppendLine($"- **SQL tried**:");
                    sb.AppendLine("```sql");
                    sb.AppendLine(h.OptimizeSql.Length > 2000 ? h.OptimizeSql[..2000] + "\n-- (truncated)" : h.OptimizeSql);
                    sb.AppendLine("```");
                }

                if (!string.IsNullOrWhiteSpace(h.ErrorMessage))
                    sb.AppendLine($"- **Error**: {h.ErrorMessage}");

                sb.AppendLine();
            }
        }

        var attemptNumber = priorHypotheses.Count + 1;
        sb.AppendLine($"This is attempt {attemptNumber} of {iteration.MaxNumberOfHypotheses}. Try a materially different approach when appropriate.");
        sb.AppendLine();
        sb.AppendLine("Analyse the database using the available tools, then propose your optimisation as a JSON response matching the required format.");

        return sb.ToString();
    }

    public static string BuildFixPrompt(
        string failedSql,
        string errorMessage,
        bool isRevert,
        string? originalOptimizeSql = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"The following {(isRevert ? "revert" : "optimisation")} SQL script failed with an error. Please fix it.");
        sb.AppendLine();
        sb.AppendLine("## Failed SQL Script");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(failedSql);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Error Message");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(errorMessage);
        sb.AppendLine("```");

        if (isRevert && !string.IsNullOrWhiteSpace(originalOptimizeSql))
        {
            sb.AppendLine();
            sb.AppendLine("## Original Optimisation SQL (this is what the revert needs to undo)");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(originalOptimizeSql);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("## Important");
        sb.AppendLine();
        sb.AppendLine("- In two-part names like [Prefix].[ObjectName], assume Prefix is the SCHEMA name, not the database name.");
        sb.AppendLine("- Handle constraint conflicts (temporarily drop/re-add constraints if needed).");
        sb.AppendLine("- Use IF EXISTS checks where possible.");
        if (isRevert)
            sb.AppendLine("- The revert must fully undo all optimisation changes.");
        sb.AppendLine();
        sb.AppendLine("## Required Response Format");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"description\": \"What was fixed and why\",");
        sb.AppendLine("  \"optimize_sql\": \"Corrected optimisation SQL (or empty if fixing revert only)\",");
        sb.AppendLine("  \"revert_sql\": \"Corrected revert SQL\"");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    public static string BuildCombinedPrompt(
        IReadOnlyList<Hypothesis> completedHypotheses,
        string? schemaDiscoveryMarkdown = null,
        string? baselinePerformanceSummary = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Several optimization attempts have already been performed on this workload.");
        sb.AppendLine("Some improved performance, some regressed, and some failed.");
        sb.AppendLine();
        sb.AppendLine("Create one ULTIMATE optimization script that combines the most effective compatible strategies identified so far.");
        sb.AppendLine();

        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Analyse which techniques actually helped (positive improvement %).");
        sb.AppendLine("2. Combine only compatible and likely additive strategies.");
        sb.AppendLine("3. Do NOT combine conflicting structures (e.g. two different clustered indexes on the same table).");
        sb.AppendLine("4. Prefer coherent design over stacking marginal changes.");
        sb.AppendLine("5. The combined optimisation must be fully revertible.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(schemaDiscoveryMarkdown))
        {
            sb.AppendLine("## Schema Information");
            sb.AppendLine();
            sb.AppendLine(schemaDiscoveryMarkdown);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(baselinePerformanceSummary))
        {
            sb.AppendLine("## Baseline Performance");
            sb.AppendLine();
            sb.AppendLine(baselinePerformanceSummary);
            sb.AppendLine();
        }

        sb.AppendLine("## Previous Results");
        sb.AppendLine();
        sb.AppendLine("Where a benchmark run id is shown, call GetBenchmarkRunDetails(id) for the full IO statistics and server messages, and GetBenchmarkRunPlanXml(id, planIndex) for the actual execution plan XML of that run.");
        sb.AppendLine();

        foreach (var h in completedHypotheses.OrderByDescending(h => h.ImpovementPercentage))
        {
            var outcome = ClassifyOutcome(h);
            sb.AppendLine($"### {outcome} (improvement: {h.ImpovementPercentage:+0.##;-0.##;0}%)");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(h.Description))
                sb.AppendLine($"**Description**: {h.Description}");
            if (h.BenchmarkRunAfter != null)
                sb.AppendLine($"**Measured (after)**: CPU {h.BenchmarkRunAfter.TotalServerCpuTimeMs}ms, Elapsed {h.BenchmarkRunAfter.TotalServerElapsedTimeMs}ms, Logical Reads {h.BenchmarkRunAfter.TotalLogicalReads} (benchmark run {(int)h.BenchmarkRunAfter.Id})");
            if (!string.IsNullOrWhiteSpace(h.OptimizeSql))
            {
                sb.AppendLine("**Optimisation SQL**:");
                sb.AppendLine("```sql");
                sb.AppendLine(h.OptimizeSql);
                sb.AppendLine("```");
            }
            if (!string.IsNullOrWhiteSpace(h.RevertSql))
            {
                sb.AppendLine("**Revert SQL**:");
                sb.AppendLine("```sql");
                sb.AppendLine(h.RevertSql);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Required Response Format");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"description\": \"Description of the combined optimization strategy\",");
        sb.AppendLine("  \"optimize_sql\": \"Combined T-SQL optimization script\",");
        sb.AppendLine("  \"revert_sql\": \"T-SQL script that reverts all combined changes\"");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static string Clip(string value, int maxChars = MaxFindingFieldChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n… (truncated)";

    /// <summary>
    /// Formats a benchmark run as a markdown summary for the agent prompt: key timings,
    /// full IO statistics, server messages (STATISTICS IO/TIME) and a pointer to the
    /// benchmark tools for fetching the captured actual execution plan XML.
    /// </summary>
    public static string FormatBenchmarkRunSummary(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Benchmark run id: {(int)run.Id}");
        sb.AppendLine($"- CPU time (median): {run.TotalServerCpuTimeMs} ms");
        sb.AppendLine($"- Elapsed time (median): {run.TotalServerElapsedTimeMs} ms");
        sb.AppendLine($"- Scan count: {run.TotalScanCount}");
        sb.AppendLine($"- Logical reads: {run.TotalLogicalReads}");
        sb.AppendLine($"- Physical reads: {run.TotalPhysicalReads}");
        sb.AppendLine($"- Read-ahead reads: {run.TotalReadAheadReads}");
        if (run.TotalLobLogicalReads > 0 || run.TotalLobPhysicalReads > 0)
            sb.AppendLine($"- LOB reads: {run.TotalLobLogicalReads} logical, {run.TotalLobPhysicalReads} physical");
        sb.AppendLine($"- Actual execution plans captured: {run.ActualPlanXml.Count}");
        sb.AppendLine();
        sb.AppendLine($"Use GetBenchmarkRunDetails({(int)run.Id}) for the full IO breakdown and server messages, and GetBenchmarkRunPlanXml({(int)run.Id}, planIndex) for the actual execution plan XML (runtime row counts, spills, warnings).");

        if (!string.IsNullOrWhiteSpace(run.Messages))
        {
            sb.AppendLine();
            sb.AppendLine("Server messages (STATISTICS IO/TIME output):");
            sb.AppendLine("```");
            sb.AppendLine(Clip(run.Messages.Trim()));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static string ClassifyOutcome(Hypothesis h)
    {
        if (h.Status == HypothesisState.Failed)
            return "FAILED";
        if (h.ImpovementPercentage > 5)
            return "GOOD";
        if (h.ImpovementPercentage < -5)
            return "BAD";
        return "NO SIGNIFICANT CHANGE";
    }
}
