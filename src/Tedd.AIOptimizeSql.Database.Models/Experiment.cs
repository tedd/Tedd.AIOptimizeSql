using System.ComponentModel.DataAnnotations;

using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum ExperimentId { }

public record Experiment
{
    [Key]
    public ExperimentId Id { get; set; }

    public DatabaseConnectionId? DatabaseConnectionId { get; set; }
    public DatabaseConnection? DatabaseConnection { get; set; }

    /// <summary>
    /// Gets or sets the name associated with the entity.
    /// </summary>
    /// <remarks>The name is required and must not exceed 1,024 characters in length.</remarks>
    [Required, MaxLength(1024)]
    public required string Name { get; set; }

    /// <summary>
    /// Description is for humans and is not used by the system. It can be null or empty if not needed.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Instructions are for AI, here you can provide specific instructions or context that the AI should consider when optimizing SQL queries. It can be null or empty if you want AI to figure it out itself.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// SQL code for initializing before optimization runs, for example, you can set up temp tables or specific database settings. It can be null or empty if not needed.
    /// </summary>
    public string? ExperimentPreRunSql { get; set; }

    /// <summary>
    /// SQL code for cleaning up. Executed after each run. For example, you can drop temp tables or reset database settings. It can be null or empty if not needed.
    /// </summary>
    public string? ExperimentPostRunSql { get; set; }

    /// <summary>
    /// SQL code to run before each hypothesis.
    /// </summary>
    public string? HypothesisPreRunSql { get; set; }
    /// <summary>
    /// SQL code to run after each hypothesis, for example, you can clean up temp tables or reset database settings. It can be null or empty if not needed.
    /// </summary>
    public string? HypothesisPostRunSql { get; set; }

    /// <summary>
    /// SQL code for benchmarking the performance of SQL queries. This is the SQL that will be used to measure the performance of the original and optimized queries. It can be null or empty if you want AI to figure it out itself.
    /// </summary>
    public string? BenchmarkSql { get; set; }

    /// <summary>
    /// How much isolation this experiment gets. <see cref="ExperimentIsolationMode.None"/>
    /// runs against the target database as-is; the other modes provision a sandbox before
    /// the first hypothesis and tear it down when the iteration finishes.
    /// </summary>
    public ExperimentIsolationMode IsolationMode { get; set; } = ExperimentIsolationMode.None;

    /// <summary>
    /// Schema the sandbox copies live in when <see cref="IsolationMode"/> is
    /// <see cref="ExperimentIsolationMode.SandboxSchema"/> (e.g. <c>aiopt_sandbox</c>).
    /// </summary>
    [MaxLength(128)]
    public string? SandboxSchemaName { get; set; }

    /// <summary>
    /// Database provisioned on the same server when <see cref="IsolationMode"/> is
    /// <see cref="ExperimentIsolationMode.CloneDatabase"/>. The engine swaps the connection
    /// string's initial catalog to this name for the whole experiment.
    /// </summary>
    [MaxLength(128)]
    public string? SandboxDatabaseName { get; set; }

    /// <summary>
    /// Script that builds the sandbox (schemas, tables, indexes, constraints, data copies,
    /// rewritten modules). Runs once, before schema discovery and the baseline benchmark.
    /// Ignored when <see cref="IsolationMode"/> is <see cref="ExperimentIsolationMode.None"/>.
    /// </summary>
    public string? SandboxSetupSql { get; set; }

    /// <summary>
    /// Script that removes everything <see cref="SandboxSetupSql"/> created. Runs when the
    /// iteration finishes, including after a failure.
    /// </summary>
    public string? SandboxTeardownSql { get; set; }

    /// <summary>
    /// How the benchmark query's output is proven unchanged by each optimization.
    /// </summary>
    public OutputVerificationMode OutputVerificationMode { get; set; } = OutputVerificationMode.None;

    /// <summary>
    /// Script that returns a single scalar fingerprint of <see cref="BenchmarkSql"/>'s output.
    /// Executed once for the baseline and once per hypothesis (after its benchmark, before
    /// revert); a differing value means the optimization changed the result.
    /// </summary>
    public string? OutputVerificationSql { get; set; }

    /// <summary>
    /// Which AI connection to use.
    /// </summary>
    public AIConnectionId? AIConnectionId { get; set; }
    public AIConnection? AIConnection { get; set; }

    public List<ResearchIteration> ResearchIterations { get; set; } = new List<ResearchIteration>();

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Last modified UTC
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}