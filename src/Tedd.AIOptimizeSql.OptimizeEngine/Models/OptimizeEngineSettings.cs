namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

public sealed class OptimizeEngineSettings
{
    public int QueuePollIntervalSeconds { get; set; } = 10;
    public int BatchStateCheckIntervalSeconds { get; set; } = 5;
    public int MaxToolResponseBytes { get; set; } = 524_288;

    /// <summary>Timed benchmark iterations per hypothesis (after cache clearing each time).</summary>
    public int BenchmarkIterations { get; set; } = 5;

    /// <summary>Pre-measurement warm-up iterations (timings discarded).</summary>
    public int WarmUpIterations { get; set; } = 1;

    /// <summary>Max retries when AI-generated optimize or revert SQL fails to execute.</summary>
    public int AiMaxRetries { get; set; } = 3;

    /// <summary>Milliseconds to pause after cache clearing before each timed measurement.</summary>
    public int PostClearStabilizationMs { get; set; } = 1500;
}
