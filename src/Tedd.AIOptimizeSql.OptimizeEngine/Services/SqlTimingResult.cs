namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// CPU and elapsed time (ms) parsed from SET STATISTICS TIME output.
/// </summary>
public sealed class SqlTimingResult
{
    public int CpuTimeMs { get; set; }
    public int ElapsedTimeMs { get; set; }
}
