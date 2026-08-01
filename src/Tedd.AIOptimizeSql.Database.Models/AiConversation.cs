using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum AiConversationId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// One AI conversation and what it cost. The conversation itself — the message
/// history — is owned by the agent harness (the OpenAI/agent session), not by this
/// table; what is persisted here is the usage ledger: which connection talked to
/// which model, for what, over how many requests, and how many tokens it burned.
/// </summary>
/// <remarks>
/// The links back to the analysis/iteration/hypothesis that caused the spend are plain
/// ids rather than foreign keys on purpose: deleting an experiment must not erase the
/// record of what it cost, and a ledger with no cascade paths cannot deadlock the
/// multi-path delete rules the rest of the model already works around.
/// </remarks>
public record AiConversation
{
    [Key]
    public AiConversationId Id { get; set; }

    /// <summary>Database the work was scoped to; the token usage view filters on this.</summary>
    public DatabaseConnectionId? DatabaseConnectionId { get; set; }
    public DatabaseConnection? DatabaseConnection { get; set; }

    /// <summary>AI connection used. Cleared if the connection is later deleted.</summary>
    public AIConnectionId? AIConnectionId { get; set; }
    public AIConnection? AIConnection { get; set; }

    public AiConversationKind Kind { get; set; } = AiConversationKind.DatabaseAnalysis;

    public AiConversationState State { get; set; } = AiConversationState.Running;

    /// <summary>Provider snapshot, so history stays readable after the AI connection changes.</summary>
    public AiProvider? Provider { get; set; }

    [MaxLength(128)]
    public string? Model { get; set; }

    /// <summary>Human-readable label, e.g. "Analysis: Nightly batch" or "Hypothesis #12".</summary>
    [MaxLength(512)]
    public string? Title { get; set; }

    /// <summary>Number of agent requests (initial run plus continuations) in this conversation.</summary>
    public int RequestCount { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>
    /// Total as reported by the provider. Providers that count reasoning or cached tokens
    /// separately report a total larger than input + output, so this is stored rather than derived.
    /// </summary>
    public long TotalTokens { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>Related database analysis, when <see cref="Kind"/> is an analysis run.</summary>
    public int? RelatedDatabaseAnalysisId { get; set; }

    /// <summary>Related experiment, for every experiment-scoped conversation.</summary>
    public int? RelatedExperimentId { get; set; }

    public int? RelatedResearchIterationId { get; set; }

    public int? RelatedHypothesisId { get; set; }

    /// <summary>Failure detail when <see cref="State"/> is <see cref="AiConversationState.Failed"/>.</summary>
    public string? LastMessage { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
