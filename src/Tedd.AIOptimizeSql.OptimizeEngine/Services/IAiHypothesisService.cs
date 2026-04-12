using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public interface IAiHypothesisService
{
    /// <summary>
    /// Generates hypotheses in a loop until the iteration's <see cref="ResearchIteration.MaxNumberOfHypotheses"/>
    /// is reached, the iteration state changes to stop/pause, or an unrecoverable error occurs.
    /// Each hypothesis is persisted as it is created, and the iteration status is kept up to date.
    /// </summary>
    Task RunIterationAsync(ResearchIterationId iterationId, CancellationToken cancellationToken = default);
}
