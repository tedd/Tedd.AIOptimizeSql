namespace Tedd.AIOptimizeSql.WebUI.Mappers;

public sealed record HypothesisBatchRunStatsSource(
    int TotalHypotheses,
    int Improvements,
    int Regressions,
    long TotalTimeMs);
