using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum HypothesisLogId { }

/// <summary>
/// Append-only activity line for a hypothesis (worker pickup, generation steps, errors).
/// </summary>
public record HypothesisLog
{
    [Key]
    public HypothesisLogId Id { get; set; }

    public required HypothesisId HypothesisId { get; set; }

    public Hypothesis? Hypothesis { get; set; }

    /// <summary>
    /// Optional short tag, e.g. QueueMonitor, HypothesisService, ProcessingEngine.
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
}
