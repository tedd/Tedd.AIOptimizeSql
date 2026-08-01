using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>Everything the sandbox generator needs to script a sandbox for one experiment.</summary>
public sealed record SandboxScriptRequest
{
    public ExperimentIsolationMode IsolationMode { get; init; } = ExperimentIsolationMode.None;

    /// <summary>Which configured database this is for, so the AI spend lands on its token ledger.</summary>
    public DatabaseConnectionId? DatabaseConnectionId { get; init; }

    /// <summary>Schema the copies live in, for <see cref="ExperimentIsolationMode.SandboxSchema"/>.</summary>
    public string? SandboxSchemaName { get; init; }

    /// <summary>Database the copies live in, for <see cref="ExperimentIsolationMode.CloneDatabase"/>.</summary>
    public string? SandboxDatabaseName { get; init; }

    /// <summary>
    /// The benchmark, used to work out which views, functions and procedures the sandbox has to
    /// contain for it to run. May be empty, in which case only tables are copied.
    /// </summary>
    public string BenchmarkSql { get; init; } = "";

    /// <summary>Base tables the user kept ticked. Only <c>Include</c>d ones are copied.</summary>
    public IReadOnlyList<BlueprintTable> Tables { get; init; } = [];
}

/// <summary>A generated setup/teardown pair, plus everything the user should know about it.</summary>
public sealed record SandboxScripts
{
    public string? Setup { get; init; }
    public string? Teardown { get; init; }

    /// <summary>Gaps and hazards: objects that could not be copied, names that collide, and so on.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>One line for the UI: what was generated.</summary>
    public string Summary { get; init; } = "";

    public static SandboxScripts NotIsolated { get; } = new()
    {
        Summary = "Isolation mode is None — nothing is provisioned, so there are no sandbox scripts."
    };
}
