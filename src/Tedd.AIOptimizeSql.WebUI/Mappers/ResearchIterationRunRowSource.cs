using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

public sealed record ResearchIterationRunRowSource(
    ResearchIteration Iteration,
    int HypothesisCount,
    long TotalTimeMs);
