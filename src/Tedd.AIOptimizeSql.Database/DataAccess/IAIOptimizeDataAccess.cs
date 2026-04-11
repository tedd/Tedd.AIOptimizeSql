using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public interface IAIOptimizeDataAccess
{
    Task<(IReadOnlyList<HypothesisBatchListRow> Items, int TotalCount)> GetHypothesisBatchesPageAsync(
        int skip,
        int take,
        string? sortLabel,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken = default);

    Task<HypothesisBatch?> GetHypothesisBatchForEditAsync(HypothesisBatchId id, CancellationToken cancellationToken = default);

    Task<HypothesisBatchId> CreateHypothesisBatchAsync(
        ExperimentId experimentId,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default);

    Task UpdateHypothesisBatchEditableFieldsAsync(
        HypothesisBatchId id,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates state and keeps <see cref="RunQueue"/> in sync (one row when <see cref="HypothesisBatchState.Queued"/>, none otherwise).
    /// </summary>
    Task SetHypothesisBatchStateAsync(
        HypothesisBatchId id,
        HypothesisBatchState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies AI settings from the parent experiment, sets state to <see cref="HypothesisBatchState.Running"/>, clears queue rows, and stamps <see cref="HypothesisBatch.StartedAt"/>.
    /// </summary>
    Task BeginHypothesisBatchRunAsync(HypothesisBatchId id, CancellationToken cancellationToken = default);

    Task DeleteHypothesisBatchAsync(HypothesisBatchId id, CancellationToken cancellationToken = default);

    Task ClearAiConnectionReferencesAsync(AIConnectionId id, CancellationToken cancellationToken = default);
}
