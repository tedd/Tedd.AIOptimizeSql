using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum ResearchIterationLogId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// Append-only activity line for a research iteration (schema discovery, baseline benchmark
/// progress, executed SQL, timings, errors). Covers the phases that run before any hypothesis
/// exists, where <see cref="HypothesisLog"/> has no row to attach to.
/// </summary>
public record ResearchIterationLog
{
    [Key]
    public ResearchIterationLogId Id { get; set; }

    public required ResearchIterationId ResearchIterationId { get; set; }

    public ResearchIteration? ResearchIteration { get; set; }

    /// <summary>
    /// Optional short tag, e.g. Benchmark, SchemaDiscovery, ProcessingEngine.
    /// </summary>
    [MaxLength(64)]
    public string? Source { get; set; }

    /// <summary>
    /// Log body (may be long for SQL scripts or server statistics output).
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
