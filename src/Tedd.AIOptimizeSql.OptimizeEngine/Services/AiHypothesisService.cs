using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class AiHypothesisService(
    AiAgentFactory agentFactory,
    IOptions<OptimizeEngineSettings> settings,
    ILoggerFactory loggerFactory) : IAiHypothesisService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AiHypothesisService>();

    public async Task<Hypothesis> GenerateHypothesisAsync(
        HypothesisBatch batch,
        IReadOnlyList<Hypothesis> priorHypotheses,
        CancellationToken cancellationToken = default)
    {
        var experiment = batch.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded on the batch.");
        var aiConnection = batch.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded on the batch.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded on the experiment.");

        var executor = DatabaseExecutorFactory.Create(
            new BenchmarkConfig { DatabaseType = "MSSQL" },
            msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, cancellationToken);
        using var toolWrapper = new SqlToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<SqlToolWrapper>());

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlQuery, nameof(toolWrapper.ExecuteSqlQuery)),
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlNonQuery, nameof(toolWrapper.ExecuteSqlNonQuery)),
            AIFunctionFactory.Create(toolWrapper.GetExecutionPlan, nameof(toolWrapper.GetExecutionPlan)),
        };

        var instructions = BuildInstructions(experiment, batch, priorHypotheses);
        var agent = agentFactory.Create(aiConnection, instructions, tools);

        var sw = Stopwatch.StartNew();
        var prompt = BuildPrompt(experiment, batch, priorHypotheses);

        _logger.LogInformation("Invoking AI agent for batch {BatchId}, hypothesis #{Number}",
            batch.Id, priorHypotheses.Count + 1);

        var result = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        sw.Stop();

        _logger.LogInformation("AI agent returned in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        return new Hypothesis
        {
            HypothesisBatchId = batch.Id,
            Description = result?.ToString() ?? "(no response)",
            TimeUsedMs = sw.ElapsedMilliseconds,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static string BuildInstructions(Experiment experiment, HypothesisBatch batch, IReadOnlyList<Hypothesis> priorHypotheses)
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

    private static string BuildPrompt(Experiment experiment, HypothesisBatch batch, IReadOnlyList<Hypothesis> priorHypotheses)
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
