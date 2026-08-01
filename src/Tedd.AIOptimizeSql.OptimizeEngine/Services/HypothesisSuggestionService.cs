using System.Text;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <inheritdoc />
public sealed class HypothesisSuggestionService(
    AiAgentFactory agentFactory,
    AiConversationTracker conversationTracker,
    ILogger<HypothesisSuggestionService> logger) : IHypothesisSuggestionService
{
    /// <summary>Schema context dominates the prompt; past this it is truncated with a note.</summary>
    private const int MaxSchemaContextChars = 40_000;

    /// <inheritdoc />
    public async Task<HypothesisSuggestion> SuggestAsync(
        AIConnection aiConnection,
        HypothesisSuggestionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aiConnection);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BenchmarkSql))
            return new HypothesisSuggestion(null, null, null,
                "The experiment has no benchmark SQL yet, so there is nothing to optimize.");

        var conversation = await conversationTracker.StartAsync(new AiConversationStart
        {
            Kind = AiConversationKind.HypothesisSuggestion,
            AiConnection = aiConnection,
            DatabaseConnectionId = request.DatabaseConnectionId,
            Title = "Draft a hypothesis",
            RelatedExperimentId = request.ExperimentId,
            RelatedResearchIterationId = request.ResearchIterationId,
        }, ct);

        try
        {
            var agent = agentFactory.Create(aiConnection, BuildInstructions(request), new List<AITool>());
            var response = await agent.RunAsync(BuildPrompt(request), cancellationToken: ct);
            conversation.Record(response?.Usage);
            await conversation.CompleteAsync(CancellationToken.None);

            var parsed = AiResponseParser.ParseHypothesisResponse(response?.ToString());
            if (parsed is null)
                return new HypothesisSuggestion(null, null, null,
                    "The AI replied with something that is not the expected JSON, so nothing was filled in.");

            // A hypothesis with no revert is not a hypothesis, it is a permanent change. Say so
            // rather than handing the user a form that looks complete.
            var missingRevert = !request.AnalyzeOnly
                && !string.IsNullOrWhiteSpace(parsed.Optimize_sql)
                && string.IsNullOrWhiteSpace(parsed.Revert_sql);

            return new HypothesisSuggestion(
                parsed.Description,
                parsed.Optimize_sql,
                parsed.Revert_sql,
                missingRevert
                    ? "The AI proposed an optimization but no revert script. Write one before running this hypothesis — without it the change cannot be undone."
                    : null);
        }
        catch (OperationCanceledException)
        {
            await conversation.FailAsync("Cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await conversation.FailAsync(ex.Message, CancellationToken.None);
            logger.LogError(ex, "Could not draft a hypothesis with the AI");
            return new HypothesisSuggestion(null, null, null, $"The AI could not be reached: {ex.Message}");
        }
    }

    private static string BuildInstructions(HypothesisSuggestionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Microsoft SQL Server performance engineer proposing ONE concrete optimization for a specific query.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("* T-SQL for Microsoft SQL Server only.");
        sb.AppendLine("* Propose exactly one change, not a list of ideas. It must be something whose effect can be measured by re-running the benchmark.");
        sb.AppendLine("* Every identifier is bracket-quoted and schema-qualified: [dbo].[Orders].");

        if (request.AnalyzeOnly)
        {
            sb.AppendLine("* This connection is ANALYZE-ONLY: it may not modify the database at all. Return an empty optimize_sql and revert_sql, and put the recommendation in the description only.");
        }
        else
        {
            sb.AppendLine("* optimize_sql applies the change; revert_sql undoes it completely. Both are required, and revert_sql must succeed even if optimize_sql only partly ran.");
            sb.AppendLine("* Do not modify data. Schema and physical-design changes (indexes, statistics, hints) only.");
        }

        sb.AppendLine();
        sb.AppendLine("Reply with JSON only, no prose and no markdown fences:");
        sb.AppendLine("""{"description": "...", "optimize_sql": "...", "revert_sql": "..."}""");
        return sb.ToString();
    }

    private static string BuildPrompt(HypothesisSuggestionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark query being optimized");
        sb.AppendLine("```sql");
        sb.AppendLine(request.BenchmarkSql.Trim());
        sb.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            sb.AppendLine();
            sb.AppendLine("# Constraints from the experiment");
            sb.AppendLine(request.Instructions.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Hints))
        {
            sb.AppendLine();
            sb.AppendLine("# Hints for this iteration");
            sb.AppendLine(request.Hints.Trim());
        }

        if (request.AlreadyTried.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Already tried in this iteration — propose something different");
            foreach (var tried in request.AlreadyTried)
                sb.AppendLine($"* {tried}");
        }

        if (!string.IsNullOrWhiteSpace(request.SchemaContextMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine("# Schema context");
            var schema = request.SchemaContextMarkdown.Trim();
            if (schema.Length > MaxSchemaContextChars)
                schema = schema[..MaxSchemaContextChars] + "\n\n… (schema context truncated)";
            sb.AppendLine(schema);
        }

        sb.AppendLine();
        sb.AppendLine("Propose the single optimization you expect to help this query the most, and return it as JSON.");
        return sb.ToString();
    }
}
