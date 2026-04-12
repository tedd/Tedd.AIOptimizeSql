using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public interface IAiHypothesisService
{
    /// <summary>
    /// Generates hypotheses in a loop until the batch's <see cref="HypothesisBatch.MaxNumberOfHypotheses"/>
    /// is reached, the batch state changes to stop/pause, or an unrecoverable error occurs.
    /// Each hypothesis is persisted as it is created, and the batch status is kept up to date.
    /// </summary>
    Task RunBatchAsync(HypothesisBatchId batchId, CancellationToken cancellationToken = default);
}
