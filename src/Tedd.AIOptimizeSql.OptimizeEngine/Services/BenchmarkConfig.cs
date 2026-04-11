namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Configuration for database benchmarking (executor factory and cache timing).
/// </summary>
public sealed class BenchmarkConfig
{
    /// <summary>Database engine identifier, e.g. <c>MSSQL</c>.</summary>
    public string DatabaseType { get; set; } = "MSSQL";

    /// <summary>Milliseconds to sleep after clearing caches before the next measurement.</summary>
    public int PostClearStabilizationMs { get; set; } = 250;
}
