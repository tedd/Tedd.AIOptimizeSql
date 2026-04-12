using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public sealed record ResearchIterationListRow(
    ResearchIterationId Id,
    ExperimentId ExperimentId,
    string ExperimentName,
    int HypothesisCount,
    double? BestImprovementPct,
    int ImprovementCount,
    ResearchIterationState State,
    DateTime? StartedAt,
    DateTime? EndedAt,
    string? LastMessage,
    string? Hints,
    DateTime CreatedAt,
    string? AiModelUsed);
