namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>One column of an ad-hoc result set.</summary>
public sealed record AdHocColumn
{
    public required string Name { get; init; }
    public required string ClrTypeName { get; init; }
}

/// <summary>One result set returned by an ad-hoc batch.</summary>
public sealed record AdHocResultSet
{
    public required IReadOnlyList<AdHocColumn> Columns { get; init; }

    /// <summary>Rows as display strings; <c>null</c> means SQL NULL.</summary>
    public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

    /// <summary>True when the reader was stopped at the row cap and more rows exist.</summary>
    public bool Truncated { get; init; }

    /// <summary>Index of the batch (GO-separated) this result set came from.</summary>
    public int BatchIndex { get; init; }
}

/// <summary>Outcome of running one ad-hoc SQL text in the query window.</summary>
public sealed record AdHocQueryResult
{
    public required bool Success { get; init; }

    public IReadOnlyList<AdHocResultSet> ResultSets { get; init; } = [];

    /// <summary>PRINT / RAISERROR / statistics output, in arrival order.</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>Rows reported affected across all batches, or -1 when not applicable.</summary>
    public int RowsAffected { get; init; }

    public long ElapsedMilliseconds { get; init; }

    /// <summary>Populated when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>1-based line within the submitted text that the server blamed, when known.</summary>
    public int? ErrorLineNumber { get; init; }

    /// <summary>Showplan XML when the caller asked for a plan.</summary>
    public IReadOnlyList<string> PlanXml { get; init; } = [];

    /// <summary>True when the analyze-only guard rejected the SQL before it ran.</summary>
    public bool BlockedByReadOnlyGuard { get; init; }
}

/// <summary>What to run and how.</summary>
public sealed record AdHocQueryRequest
{
    public required string Sql { get; init; }

    /// <summary>Rows kept per result set before truncating. Guards the browser against a runaway SELECT.</summary>
    public int MaxRows { get; init; } = 1000;

    public int CommandTimeoutSeconds { get; init; } = 120;

    /// <summary>Capture the estimated plan instead of executing (<c>SET SHOWPLAN_XML ON</c>).</summary>
    public bool EstimatedPlanOnly { get; init; }

    /// <summary>Execute and capture the actual plan (<c>SET STATISTICS XML ON</c>).</summary>
    public bool IncludeActualPlan { get; init; }

    /// <summary>
    /// Enforce <c>ReadOnlySqlGuard</c> before executing. Set from the connection's
    /// <c>AnalyzeOnly</c> flag; callers must not weaken it for an analyze-only connection.
    /// </summary>
    public bool ReadOnlyOnly { get; init; }
}
