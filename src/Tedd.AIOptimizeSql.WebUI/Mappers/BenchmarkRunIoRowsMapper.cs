using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.WebUI.ViewModels;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

/// <summary>
/// Builds I/O metric rows for benchmark display (one logical row per scalar on <see cref="BenchmarkRun"/>).
/// </summary>
public static class BenchmarkRunIoRowsMapper
{
    public static IReadOnlyList<BenchmarkRunIoRowViewModel> FromRun(BenchmarkRun run) =>
        FromMetrics(BenchmarkRunIoMetricsMapper.ToMetricsSource(run));

    private static IReadOnlyList<BenchmarkRunIoRowViewModel> FromMetrics(BenchmarkRunIoMetricsSource m) =>
    [
        new("Scan count", m.TotalScanCount),
        new("Logical reads", m.TotalLogicalReads),
        new("Physical reads", m.TotalPhysicalReads),
        new("Page server reads", m.TotalPageServerReads),
        new("Read-ahead reads", m.TotalReadAheadReads),
        new("Page server read-ahead reads", m.TotalPageServerReadAheadReads),
        new("LOB logical reads", m.TotalLobLogicalReads),
        new("LOB physical reads", m.TotalLobPhysicalReads),
        new("LOB page server reads", m.TotalLobPageServerReads),
        new("LOB read-ahead reads", m.TotalLobReadAheadReads),
        new("LOB page server read-ahead reads", m.TotalLobPageServerReadAheadReads)
    ];
}
