using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum DatabaseAnalysisLogId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// Append-only activity line for a database analysis (collector progress, AI steps, errors).
/// </summary>
public record DatabaseAnalysisLog
{
    [Key]
    public DatabaseAnalysisLogId Id { get; set; }

    public required DatabaseAnalysisId DatabaseAnalysisId { get; set; }

    public DatabaseAnalysis? DatabaseAnalysis { get; set; }

    /// <summary>
    /// Optional short tag, e.g. AnalysisMonitor, SnapshotService, AnalysisAgent.
    /// </summary>
    [MaxLength(64)]
    public string? Source { get; set; }

    /// <summary>
    /// Log body (may be long for stack traces or AI excerpts).
    /// </summary>
    public required string Message { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when the log row is created (append-only).
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
