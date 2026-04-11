namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class OptimizeEngineSettings
{
    public int QueuePollIntervalSeconds { get; set; } = 10;
    public int BatchStateCheckIntervalSeconds { get; set; } = 5;
    public int MaxToolResponseBytes { get; set; } = 524_288;
}
