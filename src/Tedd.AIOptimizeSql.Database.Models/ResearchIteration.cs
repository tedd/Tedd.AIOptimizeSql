using System.ComponentModel.DataAnnotations;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum ResearchIterationId { }

public record ResearchIteration
{
    [Key]
    public ResearchIterationId Id { get; set; }

    public ExperimentId ExperimentId { get; set; }

    public Experiment? Experiment { get; set; }

    public string? Hints { get; set; }

    /// <summary>
    /// Snapshot of the experiment's AI connection when a run starts. Cleared if the connection is deleted.
    /// </summary>
    public AIConnectionId? AIConnectionId { get; set; }
    public AIConnection? AIConnection { get; set; }

    public AiProvider? AiProviderUsed { get; set; }
    public string? AiModelUsed { get; set; }

    public int MaxNumberOfHypotheses { get; set; } = 10;

    public ResearchIterationState State { get; set; } = ResearchIterationState.Stopped;

    /// <summary>
    /// Human-readable markdown summary produced by schema discovery.
    /// </summary>
    public string? SchemaDiscoveryMarkdown { get; set; }

    /// <summary>
    /// Serialized <c>SchemaDiscoveryResult</c> JSON for programmatic access.
    /// </summary>
    public string? SchemaDiscoveryResultJson { get; set; }

    /// <summary>
    /// JSON list of <c>[{schema, table}]</c> computed deterministically from the dependency graph.
    /// Used for data integrity checking (checksum comparison).
    /// </summary>
    public string? RegisteredBaseTables { get; set; }

    /// <summary>
    /// The baseline benchmark run (before any optimization) shared by all hypotheses in this iteration.
    /// </summary>
    public BenchmarkRunId? BaselineBenchmarkRunId { get; set; }
    public BenchmarkRun? BaselineBenchmarkRun { get; set; }

    /// <summary>
    /// The date and time the run queue started.
    /// </summary>
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? LastMessage { get; set; }

    public List<Hypothesis> Hypotheses { get; set; } = new List<Hypothesis>();

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
