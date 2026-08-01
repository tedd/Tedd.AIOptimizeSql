using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>
/// An analysis finding, flattened into what the AI needs to turn "we think X is slow" into
/// "here is the query that proves it".
/// </summary>
public sealed record FindingExperimentContext
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>DMV output, plan fragments, measurements — the numbers behind the claim.</summary>
    public string? Evidence { get; init; }

    public string? Recommendation { get; init; }

    /// <summary>The finding's suggested remediation script, if it produced one.</summary>
    public string? RecommendationSql { get; init; }

    /// <summary>Primary affected object, when the finding targets one.</summary>
    public string? ObjectSchema { get; init; }
    public string? ObjectName { get; init; }

    public string? Category { get; init; }
    public string? Severity { get; init; }

    /// <summary>Scope for the token ledger and for naming a sandbox that does not collide.</summary>
    public DatabaseConnectionId? DatabaseConnectionId { get; init; }
    public string? DatabaseName { get; init; }
}

/// <summary>
/// What the AI proposed for a finding. <see cref="Error"/> is set instead of the other fields
/// when the AI could not be used, so the wizard can say why and let the user write the query.
/// </summary>
public sealed record FindingExperimentDraft(
    string? Name,
    string? BenchmarkSql,
    string? Goal,
    string? Instructions,
    string? Error);
