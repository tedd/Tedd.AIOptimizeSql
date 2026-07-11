using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum DatabaseAnalysisId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// A discovery run against a database: the engine collects performance metrics
/// (missing indexes, fragmentation, statistics health, expensive queries, ...)
/// and an AI agent digs into problem areas, producing <see cref="AnalysisFinding"/> rows.
/// Analysis is always read-only against the target database.
/// </summary>
public record DatabaseAnalysis
{
    [Key]
    public DatabaseAnalysisId Id { get; set; }

    [Required, MaxLength(1024)]
    public required string Name { get; set; }

    public DatabaseConnectionId? DatabaseConnectionId { get; set; }
    public DatabaseConnection? DatabaseConnection { get; set; }

    public AIConnectionId? AIConnectionId { get; set; }
    public AIConnection? AIConnection { get; set; }

    /// <summary>
    /// Extra instructions or focus areas for the AI (e.g. "focus on the Orders module").
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// When true the AI gets web search / web fetch tools (requires a configured web search API key).
    /// </summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// When true the AI additionally reviews stored procedure and view definitions.
    /// </summary>
    public bool IncludeStoredProceduresAndViews { get; set; } = true;

    /// <summary>
    /// Maximum number of AI deep-dive findings to request (soft cap communicated to the agent).
    /// </summary>
    public int MaxAiFindings { get; set; } = 25;

    public DatabaseAnalysisState State { get; set; } = DatabaseAnalysisState.Draft;

    public string? LastMessage { get; set; }

    /// <summary>
    /// Serialized snapshot of the deterministic metric collection (JSON) for programmatic access.
    /// </summary>
    public string? MetricsSnapshotJson { get; set; }

    /// <summary>
    /// Markdown summary of the collected metrics fed to the AI and shown in the UI.
    /// </summary>
    public string? MetricsSummaryMarkdown { get; set; }

    /// <summary>
    /// The AI's executive summary of the analysis (markdown).
    /// </summary>
    public string? AiSummaryMarkdown { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public List<AnalysisFinding> Findings { get; set; } = new();
    public List<DatabaseAnalysisLog> Logs { get; set; } = new();

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated on any write affecting this analysis or its findings/logs (for UI refresh).
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
