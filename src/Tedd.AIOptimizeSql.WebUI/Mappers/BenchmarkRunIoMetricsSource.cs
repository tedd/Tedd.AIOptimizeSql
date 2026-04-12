namespace Tedd.AIOptimizeSql.WebUI.Mappers;

/// <summary>
/// I/O counters copied from <see cref="Tedd.AIOptimizeSql.Database.Models.BenchmarkRun"/> for table display.
/// </summary>
public sealed record BenchmarkRunIoMetricsSource(
    int TotalScanCount,
    int TotalLogicalReads,
    int TotalPhysicalReads,
    int TotalPageServerReads,
    int TotalReadAheadReads,
    int TotalPageServerReadAheadReads,
    int TotalLobLogicalReads,
    int TotalLobPhysicalReads,
    int TotalLobPageServerReads,
    int TotalLobReadAheadReads,
    int TotalLobPageServerReadAheadReads);
