using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public sealed record HypothesisBatchListRow(
    HypothesisBatchId Id,
    ExperimentId ExperimentId,
    string ExperimentName,
    int HypothesisCount,
    double? BestImprovementPct,
    int ImprovementCount,
    HypothesisBatchState State,
    DateTime? StartedAt,
    DateTime? EndedAt,
    string? LastMessage,
    string? Hints,
    DateTime CreatedAt,
    string? AiModelUsed);
