using System.Text;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

internal static class HypothesisPromptBuilder
{
    public static string BuildInstructions(
        Experiment experiment,
        ResearchIteration iteration,
        IReadOnlyList<Hypothesis> priorHypotheses,
        string? schemaDiscoveryMarkdown = null,
        string? baselinePerformanceSummary = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a MSSQL performance optimization expert.");
        sb.AppendLine("Your goal is to propose a single, concrete optimisation for the SQL workload described below.");
        sb.AppendLine("You have access to tools that let you execute SQL queries, run DDL/DML statements, inspect execution plans, and query schema metadata on the target database.");
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

        // Experiment instructions
        if (!string.IsNullOrWhiteSpace(experiment.Instructions))
        {
            sb.AppendLine("## Experiment-specific instructions");
            sb.AppendLine();
            sb.AppendLine(experiment.Instructions);
            sb.AppendLine();
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

        foreach (var h in completedHypotheses.OrderByDescending(h => h.ImpovementPercentage))
        {
            var outcome = ClassifyOutcome(h);
            sb.AppendLine($"### {outcome} (improvement: {h.ImpovementPercentage:+0.##;-0.##;0}%)");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(h.Description))
                sb.AppendLine($"**Description**: {h.Description}");
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
