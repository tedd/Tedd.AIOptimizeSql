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

    /// <summary>
    /// Deletes experiments with all dependents handled: analysis-finding links are cleared,
    /// iterations/hypotheses/logs/tasks/queue rows cascade, and benchmark runs that are no
    /// longer referenced by anything are removed. Returns the number of experiments deleted.
    /// </summary>
    Task<int> DeleteExperimentsAsync(IReadOnlyCollection<ExperimentId> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes research iterations (hypotheses/logs/tasks/queue rows cascade) and removes
    /// benchmark runs left unreferenced. Returns the number of iterations deleted.
    /// </summary>
    Task<int> DeleteResearchIterationsAsync(IReadOnlyCollection<ResearchIterationId> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes hypotheses with all dependents handled: builds-on references from surviving
    /// hypotheses are cleared first (self-referencing FK is NO ACTION), logs/tasks cascade,
    /// and benchmark runs left unreferenced are removed. Returns the number deleted.
    /// </summary>
    Task<int> DeleteHypothesesAsync(IReadOnlyCollection<HypothesisId> ids, CancellationToken cancellationToken = default);

    Task ClearAiConnectionReferencesAsync(AIConnectionId id, CancellationToken cancellationToken = default);

    Task<DateTime?> GetMaxAiConnectionModifiedAtAsync(CancellationToken cancellationToken = default);

    Task<DateTime?> GetMaxDatabaseConnectionModifiedAtAsync(CancellationToken cancellationToken = default);

    Task<DateTime?> GetMaxExperimentModifiedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Max of experiment <see cref="Experiment.ModifiedAt"/> and any research iteration under it (for experiment results view).
    /// </summary>
    Task<DateTime?> GetExperimentResultsWatermarkAsync(ExperimentId experimentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Max <see cref="ResearchIteration.ModifiedAt"/> for all iterations, or for one experiment when <paramref name="experimentId"/> is set.
    /// </summary>
    Task<DateTime?> GetResearchIterationsScopeWatermarkAsync(ExperimentId? experimentId, CancellationToken cancellationToken = default);

    Task<DateTime?> GetResearchIterationModifiedAtAsync(ResearchIterationId id, CancellationToken cancellationToken = default);

    Task<DateTime?> GetBenchmarkRunModifiedAtAsync(BenchmarkRunId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest change relevant to hypothesis detail (hypothesis row or any log line).
    /// </summary>
    Task<DateTime?> GetHypothesisDetailWatermarkAsync(HypothesisId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Max <see cref="DatabaseAnalysis.ModifiedAt"/> across all analyses (for the list view).
    /// </summary>
    Task<DateTime?> GetMaxDatabaseAnalysisModifiedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="DatabaseAnalysis.ModifiedAt"/> for one analysis (findings/log writes touch it too).
    /// </summary>
    Task<DateTime?> GetDatabaseAnalysisModifiedAtAsync(DatabaseAnalysisId id, CancellationToken cancellationToken = default);
}
