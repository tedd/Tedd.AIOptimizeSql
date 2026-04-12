namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

/// <summary>
/// Rich result from executing a SQL batch with SET STATISTICS TIME/IO/XML ON.
/// Captures timing, I/O counters, execution plans, messages, and result sets.
/// </summary>
public sealed class SqlExecutionResult
{
    // ── Parse & compile ──────────────────────────────────────────────────────

    public int ParseAndCompileCpuTimeMs { get; set; }
    public int ParseAndCompileElapsedTimeMs { get; set; }

    // ── Execution times (accumulated across batches) ─────────────────────────

    public int ExecutionCpuTimeMs { get; set; }
    public int ExecutionElapsedTimeMs { get; set; }

    // ── I/O statistics (totals across all tables) ────────────────────────────

    public int TotalScanCount { get; set; }
    public int TotalLogicalReads { get; set; }
    public int TotalPhysicalReads { get; set; }
    public int TotalPageServerReads { get; set; }
    public int TotalReadAheadReads { get; set; }
    public int TotalPageServerReadAheadReads { get; set; }
    public int TotalLobLogicalReads { get; set; }
    public int TotalLobPhysicalReads { get; set; }
    public int TotalLobPageServerReads { get; set; }
    public int TotalLobReadAheadReads { get; set; }
    public int TotalLobPageServerReadAheadReads { get; set; }

    /// <summary>Per-table I/O breakdown.</summary>
    public List<TableIoStatistics> TableIoStats { get; set; } = new();

    // ── Rows affected ────────────────────────────────────────────────────────

    public List<int> RowsAffected { get; set; } = new();

    // ── Actual execution plan XML(s) ─────────────────────────────────────────

    public List<string> ActualPlanXml { get; set; } = new();

    // ── Raw messages ─────────────────────────────────────────────────────────

    public string Messages { get; set; } = "";

    // ── Data result sets ─────────────────────────────────────────────────────

    public List<List<Dictionary<string, object?>>> ResultSets { get; set; } = new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the Total* I/O fields by summing <see cref="TableIoStats"/>.
    /// </summary>
    public void RecalculateTotals()
    {
        TotalScanCount = 0;
        TotalLogicalReads = 0;
        TotalPhysicalReads = 0;
        TotalPageServerReads = 0;
        TotalReadAheadReads = 0;
        TotalPageServerReadAheadReads = 0;
        TotalLobLogicalReads = 0;
        TotalLobPhysicalReads = 0;
        TotalLobPageServerReads = 0;
        TotalLobReadAheadReads = 0;
        TotalLobPageServerReadAheadReads = 0;

        foreach (var t in TableIoStats)
        {
            TotalScanCount += t.ScanCount;
            TotalLogicalReads += t.LogicalReads;
            TotalPhysicalReads += t.PhysicalReads;
            TotalPageServerReads += t.PageServerReads;
            TotalReadAheadReads += t.ReadAheadReads;
            TotalPageServerReadAheadReads += t.PageServerReadAheadReads;
            TotalLobLogicalReads += t.LobLogicalReads;
            TotalLobPhysicalReads += t.LobPhysicalReads;
            TotalLobPageServerReads += t.LobPageServerReads;
            TotalLobReadAheadReads += t.LobReadAheadReads;
            TotalLobPageServerReadAheadReads += t.LobPageServerReadAheadReads;
        }
    }
}
