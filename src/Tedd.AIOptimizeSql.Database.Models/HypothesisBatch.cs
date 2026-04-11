using System.ComponentModel.DataAnnotations;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum HypothesisBatchId { }
public record HypothesisBatch
{
    [Key]
    public HypothesisBatchId Id { get; set; }

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

    public HypothesisBatchState State { get; set; } = HypothesisBatchState.Stopped;

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