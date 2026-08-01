using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>
/// A fully filled-in experiment proposal produced by the Create Experiment wizard.
/// Every field maps onto <c>Experiment</c>; the wizard shows them for review and edit
/// before anything is written to the database.
/// </summary>
public sealed record ExperimentBlueprint
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Free-text guidance and guard rails handed to the optimizing AI.</summary>
    public string? Instructions { get; set; }

    /// <summary>The query being optimized. Seeded from the query window.</summary>
    public string BenchmarkSql { get; set; } = "";

    public string? ExperimentPreRunSql { get; set; }
    public string? ExperimentPostRunSql { get; set; }
    public string? HypothesisPreRunSql { get; set; }
    public string? HypothesisPostRunSql { get; set; }

    public ExperimentIsolationMode IsolationMode { get; set; } = ExperimentIsolationMode.None;
    public string? SandboxSchemaName { get; set; }
    public string? SandboxDatabaseName { get; set; }
    public string? SandboxSetupSql { get; set; }
    public string? SandboxTeardownSql { get; set; }

    public OutputVerificationMode OutputVerificationMode { get; set; } = OutputVerificationMode.UnorderedHash;
    public string? OutputVerificationSql { get; set; }

    /// <summary>Base tables the benchmark reaches, used for checksum registration and sandbox copies.</summary>
    public List<BlueprintTable> BaseTables { get; set; } = [];

    /// <summary>Plain-language explanation of what will be measured and why, shown in the review step.</summary>
    public string? MeasurementPlan { get; set; }

    /// <summary>Things the user should know before running (blocking issues first).</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>A base table the experiment depends on, as shown in the wizard's dependency step.</summary>
public sealed record BlueprintTable
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public long? RowCount { get; init; }

    /// <summary>Whether to include this table in integrity checksums and sandbox copies.</summary>
    public bool Include { get; set; } = true;

    /// <summary>Why it was included: <c>read</c>, <c>written</c>, or <c>via [dbo].[SomeView]</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>What the wizard asks the AI to fill in, and what the user already decided.</summary>
public sealed record ExperimentBlueprintRequest
{
    public required string BenchmarkSql { get; init; }

    /// <summary>Markdown schema/dependency context gathered deterministically before the AI runs.</summary>
    public required string SchemaContextMarkdown { get; init; }

    /// <summary>The user's own words about what "faster" means here. May be empty.</summary>
    public string? Goal { get; init; }

    public ExperimentIsolationMode IsolationMode { get; init; } = ExperimentIsolationMode.None;
    public OutputVerificationMode OutputVerificationMode { get; init; } = OutputVerificationMode.UnorderedHash;

    /// <summary>Tables the user kept ticked in the dependency step.</summary>
    public IReadOnlyList<BlueprintTable> BaseTables { get; init; } = [];

    /// <summary>Target database name, so generated scripts can name a sandbox that does not collide.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Which configured database this is for, so the AI spend lands on its token ledger.</summary>
    public DatabaseConnectionId? DatabaseConnectionId { get; init; }
}
