using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public interface IAIOptimizeDataAccess
{
    Task<(IReadOnlyList<ResearchIterationListRow> Items, int TotalCount)> GetResearchIterationsPageAsync(
        int skip,
        int take,
        string? sortLabel,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken = default);

    Task<ResearchIteration?> GetResearchIterationForEditAsync(ResearchIterationId id, CancellationToken cancellationToken = default);

    Task<ResearchIterationId> CreateResearchIterationAsync(
        ExperimentId experimentId,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default);

    Task UpdateResearchIterationEditableFieldsAsync(
        ResearchIterationId id,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates state and keeps <see cref="RunQueue"/> in sync (one row when <see cref="ResearchIterationState.Queued"/>, none otherwise).
    /// </summary>
    Task SetResearchIterationStateAsync(
        ResearchIterationId id,
        ResearchIterationState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies AI settings from the parent experiment, sets state to <see cref="ResearchIterationState.Running"/>, clears queue rows, and stamps <see cref="ResearchIteration.StartedAt"/>.
    /// </summary>
    Task BeginResearchIterationRunAsync(ResearchIterationId id, CancellationToken cancellationToken = default);

    Task DeleteResearchIterationAsync(ResearchIterationId id, CancellationToken cancellationToken = default);

    Task ClearAiConnectionReferencesAsync(AIConnectionId id, CancellationToken cancellationToken = default);
}
