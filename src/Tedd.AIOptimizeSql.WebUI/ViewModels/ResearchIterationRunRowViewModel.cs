using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.ViewModels;

public sealed class ResearchIterationRunRowViewModel
{
    public required ResearchIteration Iteration { get; init; }
    public int HypothesisCount { get; init; }
    public long TotalTimeMs { get; init; }
}
