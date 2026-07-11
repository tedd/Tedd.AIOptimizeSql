using System.Text;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Builds instructions and prompts for the database analysis (discovery) agent.
/// </summary>
internal static class AnalysisPromptBuilder
{
    public static string BuildInstructions(DatabaseAnalysis analysis, bool webSearchEnabled, int maxAgentRuns = 20)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a senior SQL Server performance consultant performing a full health and performance review of a database.");
        sb.AppendLine("You investigate methodically: form suspicions from the collected metrics, verify them with targeted queries and estimated execution plans, then record findings.");
        sb.AppendLine();

        sb.AppendLine("## CRITICAL: analyze-only mode");
        sb.AppendLine();
        sb.AppendLine("This analysis is strictly READ-ONLY against the target database — it may be a production system.");
        sb.AppendLine("- You can run SELECT/DMV queries and request ESTIMATED execution plans (compiled, never executed).");
        sb.AppendLine("- You can NOT create, alter, or drop anything; you can NOT modify data; you can NOT overwrite views or stored procedures; you can NOT update statistics or rebuild indexes.");
        sb.AppendLine("- Any modifying statement will be blocked by a safety guard. Do not attempt it.");
        sb.AppendLine("- Instead, put proposed changes into findings (recommendationSql) and, for changes worth measuring, propose an experiment.");
        sb.AppendLine();

        sb.AppendLine("## Your tools");
        sb.AppendLine();
        sb.AppendLine("- ExecuteSqlQuery: run read-only SQL (SELECT, DMVs, system catalog).");
        sb.AppendLine("- GetExecutionPlan: estimated execution plan XML for any query — the query is compiled but NOT executed. Use this to 'test' how queries and proposed rewrites behave without touching the database.");
        sb.AppendLine("- Performance metric tools: GetMissingIndexes, GetIndexFragmentation, GetIndexUsageStats, GetStatisticsHealth, GetTopQueries, GetStoredProcedureStats, GetWaitStatistics, GetTableSizes, GetDatabaseConfiguration, ListProceduresAndViews.");
        sb.AppendLine("- Schema tools: GetObjectDefinition (view/procedure/function source), GetObjectColumns, GetTableIndexes, GetTableStorage, GetObjectDependencies, GetTriggerInfo, GetSynonymTarget, GetObjectParameters.");
        sb.AppendLine("- ReportFinding: record each distinct finding (problem OR positive observation).");
        sb.AppendLine("- ProposeExperiment: create a benchmarkable experiment for hypotheses that deserve measured verification.");
        sb.AppendLine("- AddTask / UpdateTask / ListTasks: your work plan (see workflow below).");
        if (webSearchEnabled)
        {
            sb.AppendLine("- WebSearch / FetchWebPage: research SQL Server documentation, wait types, version-specific features, and known issues on the web.");
        }
        sb.AppendLine();

        sb.AppendLine(AgentTaskPromptSection.Build(maxAgentRuns));
        sb.AppendLine();

        sb.AppendLine("## Method");
        sb.AppendLine();
        sb.AppendLine("1. Review the collected metrics summary and the deterministic findings already recorded (do not duplicate them — deepen or correct them).");
        sb.AppendLine("2. Investigate the top expensive queries and stored procedures: read their definitions, get estimated plans, look for scans on large tables, key lookups, implicit conversions, non-sargable predicates, parameter sniffing risk.");
        if (analysis.IncludeStoredProceduresAndViews)
        {
            sb.AppendLine("3. Review the most-used stored procedures and views: read their source with GetObjectDefinition, look for SELECT *, RBAR/cursors, scalar UDFs in predicates, nested views, missing WHERE sargability, and propose concrete rewrites in recommendationSql.");
        }
        sb.AppendLine("4. Cross-check missing-index suggestions against existing indexes (avoid recommending near-duplicates; prefer extending existing indexes).");
        sb.AppendLine("5. Verify each suspicion before reporting: an estimated plan or a targeted DMV query is your evidence.");
        sb.AppendLine("6. Report GOOD findings too (severity Good): healthy configuration, well-indexed hot tables, fresh statistics. The user wants a balanced picture.");
        sb.AppendLine($"7. Report at most {Math.Max(1, analysis.MaxAiFindings)} findings; prioritize by impact. Use ImpactScore to rank importance.");
        sb.AppendLine("8. For the 1-3 most promising optimization hypotheses, call ProposeExperiment with a representative benchmark SQL so the user can measure them safely on a copy.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(analysis.Instructions))
        {
            sb.AppendLine("## User instructions for this analysis");
            sb.AppendLine();
            sb.AppendLine(analysis.Instructions);
            sb.AppendLine();
        }

        sb.AppendLine("## Final response");
        sb.AppendLine();
        sb.AppendLine("After you have recorded all findings via ReportFinding, respond with an executive summary in markdown:");
        sb.AppendLine("- One paragraph overall health assessment.");
        sb.AppendLine("- 'Top problems' — the highest-impact issues, in priority order, one line each.");
        sb.AppendLine("- 'What is healthy' — the good news.");
        sb.AppendLine("- 'Recommended next steps' — a short ordered action list.");
        sb.AppendLine("Do not repeat full finding details in the summary; the findings are stored separately.");

        return sb.ToString();
    }

    public static string BuildPrompt(
        DatabaseAnalysis analysis,
        string metricsSummaryMarkdown,
        IReadOnlyList<AnalysisFinding> deterministicFindings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Analyze this SQL Server database. Below are the metrics collected up front and the rule-based findings already recorded.");
        sb.AppendLine();
        sb.AppendLine("## Collected metrics");
        sb.AppendLine();
        sb.AppendLine(metricsSummaryMarkdown);
        sb.AppendLine();

        if (deterministicFindings.Count > 0)
        {
            sb.AppendLine("## Findings already recorded by deterministic collectors (do not duplicate)");
            sb.AppendLine();
            foreach (var f in deterministicFindings.OrderBy(f => f.Severity))
                sb.AppendLine($"- [{f.Severity}/{f.Category}] {f.Title}");
            sb.AppendLine();
        }

        sb.AppendLine("Dig into the problem areas, verify with estimated plans and targeted queries, review the expensive stored procedures and views, record your findings with ReportFinding (including Good findings), propose experiments for the best hypotheses, and finish with the executive summary.");

        return sb.ToString();
    }
}
