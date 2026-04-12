namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

/// <summary>
/// I/O counters for a single table, parsed from SET STATISTICS IO output.
/// </summary>
public sealed class TableIoStatistics
{
    public string TableName { get; set; } = "";
    public int ScanCount { get; set; }
    public int LogicalReads { get; set; }
    public int PhysicalReads { get; set; }
    public int PageServerReads { get; set; }
    public int ReadAheadReads { get; set; }
    public int PageServerReadAheadReads { get; set; }
    public int LobLogicalReads { get; set; }
    public int LobPhysicalReads { get; set; }
    public int LobPageServerReads { get; set; }
    public int LobReadAheadReads { get; set; }
    public int LobPageServerReadAheadReads { get; set; }
}
