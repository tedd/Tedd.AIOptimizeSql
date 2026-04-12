namespace Tedd.AIOptimizeSql.WebUI.Mappers;

public sealed record ResearchIterationRunStatsSource(
    int TotalHypotheses,
    int Improvements,
    int Regressions,
    long TotalTimeMs);
