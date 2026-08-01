using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>What the AI needs to know to propose one optimization for a benchmark query.</summary>
public sealed record HypothesisSuggestionRequest
{
    public required string BenchmarkSql { get; init; }

    /// <summary>The experiment's guard rails, e.g. "do not change the schema of Sales.Orders".</summary>
    public string? Instructions { get; init; }

    /// <summary>Per-iteration steer, e.g. "the previous index attempt regressed, try join order".</summary>
    public string? Hints { get; init; }

    /// <summary>Markdown schema summary from the iteration's discovery pass, when it has run.</summary>
    public string? SchemaContextMarkdown { get; init; }

    /// <summary>
    /// One line per hypothesis already tried in this iteration, so the AI proposes something new
    /// instead of repeating what has already been measured.
    /// </summary>
    public IReadOnlyList<string> AlreadyTried { get; init; } = [];

    /// <summary>Read-only connection: the proposal must not include statements that modify data.</summary>
    public bool AnalyzeOnly { get; init; }

    // Scope for the token ledger.
    public DatabaseConnectionId? DatabaseConnectionId { get; init; }
    public int? ExperimentId { get; init; }
    public int? ResearchIterationId { get; init; }
}

/// <summary>
/// A drafted hypothesis. <see cref="Error"/> is set instead of the SQL fields when the AI could
/// not be used, so the caller can say why rather than silently filling in nothing.
/// </summary>
public sealed record HypothesisSuggestion(
    string? Description,
    string? OptimizeSql,
    string? RevertSql,
    string? Error);

/// <summary>
/// Fills in a hypothesis the user is writing by hand. This is the same job the run engine does
/// on its own, exposed as a single request so the manual form has an "ask the AI" button rather
/// than an empty pair of SQL editors.
/// </summary>
public interface IHypothesisSuggestionService
{
    Task<HypothesisSuggestion> SuggestAsync(
        AIConnection aiConnection,
        HypothesisSuggestionRequest request,
        CancellationToken ct = default);
}
