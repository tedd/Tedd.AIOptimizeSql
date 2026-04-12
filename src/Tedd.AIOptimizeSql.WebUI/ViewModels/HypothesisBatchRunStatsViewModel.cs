namespace Tedd.AIOptimizeSql.WebUI.ViewModels;

public sealed class HypothesisBatchRunStatsViewModel
{
    public int TotalHypotheses { get; init; }
    public int Improvements { get; init; }
    public int Regressions { get; init; }
    public long TotalTimeMs { get; init; }
}
