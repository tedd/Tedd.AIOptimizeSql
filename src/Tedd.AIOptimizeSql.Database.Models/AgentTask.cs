using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum AgentTaskId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// One item in an AI agent's working plan. The agent creates its plan at the
/// start of a run and keeps statuses updated as it works; the run is continued
/// (up to a configurable limit) until no Pending/InProgress tasks remain.
/// Scoped to exactly one parent: a database analysis or a hypothesis.
/// </summary>
public record AgentTask
{
    [Key]
    public AgentTaskId Id { get; set; }

    /// <summary>Set when the task belongs to a database analysis run.</summary>
    public DatabaseAnalysisId? DatabaseAnalysisId { get; set; }
    public DatabaseAnalysis? DatabaseAnalysis { get; set; }

    /// <summary>Set when the task belongs to a hypothesis generation run.</summary>
    public HypothesisId? HypothesisId { get; set; }
    public Hypothesis? Hypothesis { get; set; }

    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;

    [Required, MaxLength(1024)]
    public required string Title { get; set; }

    /// <summary>Optional detail: what the task involves, or why it was cancelled.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
