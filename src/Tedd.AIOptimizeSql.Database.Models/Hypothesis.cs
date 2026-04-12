using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum HypothesisId { }

public record Hypothesis
{
    [Key] public HypothesisId Id { get; set; }

    public required ResearchIterationId ResearchIterationId { get; set; }

    public ResearchIteration? ResearchIteration { get; set; }

    public HypothesisState Status { get; set; } = HypothesisState.Pending;

    public BenchmarkRunId? BenchmarkRunIdBefore { get; set; }
    public BenchmarkRun? BenchmarkRunBefore { get; set; }
    public BenchmarkRunId? BenchmarkRunIdAfter { get; set; }
    public BenchmarkRun? BenchmarkRunAfter { get; set; }

    /// <summary>
    /// Improvement delta (before-after). Positive means improvement, negative means regression.
    /// </summary>
    public float ImpovementPercentage { get; set; } = 0;

    /// <summary>
    /// If this hypothesis is based on another hypothesis, reference it here. This can be used to build a chain of hypotheses, where each hypothesis builds on the previous one. For example, if we have a hypothesis that adding an index improves performance, and then we have another hypothesis that adding a hint on top of that index further improves performance, the second hypothesis would build on the first one.
    /// </summary>
    public HypothesisId? BuildsOnHypothesisId { get; set; }
    public Hypothesis? BuilOptimizationHypothesis { get; set; }

    public string? Description { get; set; }

    public string? ErrorMessage { get; set; }

    public long TimeUsedMs { get; set; } = 0;

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

}