using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.Mappers;

public sealed record HypothesisBatchRunRowSource(
    HypothesisBatch Run,
    int HypothesisCount,
    long TotalTimeMs);
