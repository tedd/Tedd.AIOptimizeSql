using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.ViewModels;

public sealed class HypothesisBatchRunRowViewModel
{
    public required HypothesisBatch Run { get; init; }
    public int HypothesisCount { get; init; }
    public long TotalTimeMs { get; init; }
}
