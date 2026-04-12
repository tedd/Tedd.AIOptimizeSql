using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

public static class SqlExecutionResultExtensions
{
    /// <summary>
    /// Maps a <see cref="SqlExecutionResult"/> to a new <see cref="BenchmarkRun"/>,
    /// using <paramref name="totalTimeMs"/> for the wall-clock time measured by the caller.
    /// </summary>
    public static BenchmarkRun ToBenchmarkRun(this SqlExecutionResult src, int totalTimeMs)
    {
        return new BenchmarkRun
        {
            TotalTimeMs = totalTimeMs,
            TotalServerCpuTimeMs = src.ExecutionCpuTimeMs + src.ParseAndCompileCpuTimeMs,
            TotalServerElapsedTimeMs = src.ExecutionElapsedTimeMs + src.ParseAndCompileElapsedTimeMs,

            TotalScanCount = src.TotalScanCount,
            TotalLogicalReads = src.TotalLogicalReads,
            TotalPhysicalReads = src.TotalPhysicalReads,
            TotalPageServerReads = src.TotalPageServerReads,
            TotalReadAheadReads = src.TotalReadAheadReads,
            TotalPageServerReadAheadReads = src.TotalPageServerReadAheadReads,
            TotalLobLogicalReads = src.TotalLobLogicalReads,
            TotalLobPhysicalReads = src.TotalLobPhysicalReads,
            TotalLobPageServerReads = src.TotalLobPageServerReads,
            TotalLobReadAheadReads = src.TotalLobReadAheadReads,
            TotalLobPageServerReadAheadReads = src.TotalLobPageServerReadAheadReads,

            ActualPlanXml = new List<string>(src.ActualPlanXml),
            Messages = src.Messages,
        };
    }
}
