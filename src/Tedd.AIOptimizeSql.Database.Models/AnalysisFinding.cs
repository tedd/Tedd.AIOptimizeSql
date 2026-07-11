using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum AnalysisFindingId
{
    /// <summary>
    /// In-memory sentinel only: tells EF Core to omit <c>Id</c> on INSERT so SQL Server IDENTITY can run.
    /// Never persisted; the CLR default for this enum is still <c>0</c>, so new entities must set this explicitly when inserting.
    /// </summary>
    Transient = -1,
}

/// <summary>
/// A single observation from a database analysis: a problem, a recommendation,
/// or a positive finding (<see cref="FindingSeverity.Good"/>).
/// </summary>
public record AnalysisFinding
{
    [Key]
    public AnalysisFindingId Id { get; set; }

    public required DatabaseAnalysisId DatabaseAnalysisId { get; set; }
    public DatabaseAnalysis? DatabaseAnalysis { get; set; }

    public FindingCategory Category { get; set; } = FindingCategory.Other;
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;

    [Required, MaxLength(1024)]
    public required string Title { get; set; }

    /// <summary>
    /// What was found and why it matters (markdown).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Supporting data: DMV output excerpts, plan fragments, measurements (markdown).
    /// </summary>
    public string? Evidence { get; set; }

    /// <summary>
    /// Suggested remediation in prose (markdown).
    /// </summary>
    public string? Recommendation { get; set; }

    /// <summary>
    /// Suggested remediation as a runnable T-SQL script. Never executed by the
    /// analysis itself; users can copy it or turn it into an experiment.
    /// </summary>
    public string? RecommendationSql { get; set; }

    /// <summary>
    /// Schema of the primary affected object, when the finding targets one object.
    /// </summary>
    [MaxLength(128)]
    public string? ObjectSchema { get; set; }

    /// <summary>
    /// Name of the primary affected object (table, index, procedure, view).
    /// </summary>
    [MaxLength(256)]
    public string? ObjectName { get; set; }

    /// <summary>
    /// Relative impact estimate, larger is more impactful. For missing indexes this is the
    /// SQL Server improvement measure (avg_total_user_cost * avg_user_impact * seeks+scans).
    /// </summary>
    public double ImpactScore { get; set; }

    /// <summary>
    /// Where the finding came from: a deterministic collector name or "AI".
    /// </summary>
    [MaxLength(64)]
    public string? Source { get; set; }

    /// <summary>
    /// Experiment created from this finding (via the AI's ProposeExperiment tool or the UI), if any.
    /// </summary>
    public ExperimentId? ProposedExperimentId { get; set; }
    public Experiment? ProposedExperiment { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
