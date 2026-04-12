using System.Text;

using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

internal static class HypothesisPromptBuilder
{
    public static string BuildInstructions(Experiment experiment, HypothesisBatch batch, IReadOnlyList<Hypothesis> priorHypotheses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert SQL Server performance analyst and query optimiser.");
        sb.AppendLine("Your goal is to propose a single, concrete optimisation hypothesis for the SQL workload described below.");
        sb.AppendLine("You have access to tools that let you execute SQL queries, run DDL/DML statements, and inspect execution plans on the target database.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Analyse the current schema, indexes, and execution plans before proposing changes.");
        sb.AppendLine("- Propose exactly ONE optimisation (e.g. add an index, rewrite a query, add hints, change schema).");
        sb.AppendLine("- Explain WHY you expect it to improve performance.");
        sb.AppendLine("- Do NOT execute destructive operations that could corrupt or lose data.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(experiment.Instructions))
        {
            sb.AppendLine("=== Experiment-specific instructions ===");
            sb.AppendLine(experiment.Instructions);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(experiment.BenchmarkSql))
        {
            sb.AppendLine("=== Benchmark SQL (the query to optimise) ===");
            sb.AppendLine(experiment.BenchmarkSql);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildPrompt(HypothesisBatch batch, IReadOnlyList<Hypothesis> priorHypotheses)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(batch.Hints))
        {
            sb.AppendLine("=== Additional hints for this batch ===");
            sb.AppendLine(batch.Hints);
            sb.AppendLine();
        }

        if (priorHypotheses.Count > 0)
        {
            sb.AppendLine("=== Prior hypotheses in this batch (avoid duplicating these) ===");
            for (var i = 0; i < priorHypotheses.Count; i++)
            {
                var h = priorHypotheses[i];
                sb.AppendLine($"Hypothesis #{i + 1} (improvement: {h.ImpovementPercentage:+0.##;-0.##;0}%):");
                sb.AppendLine(h.Description ?? "(no description)");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Please analyse the database and propose your next optimisation hypothesis.");
        sb.AppendLine("Use the available tools to inspect the schema, execution plans, and data as needed.");
        sb.AppendLine("Return your hypothesis as a clear description of the proposed change and your reasoning.");

        return sb.ToString();
    }
}
