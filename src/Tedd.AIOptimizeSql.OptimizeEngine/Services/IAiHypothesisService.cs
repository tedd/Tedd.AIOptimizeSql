using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public interface IAiHypothesisService
{
    /// <summary>
    /// Generates hypotheses in a loop until the iteration's <see cref="ResearchIteration.MaxNumberOfHypotheses"/>
    /// is reached, the iteration state changes to stop/pause, or an unrecoverable error occurs.
    /// Each hypothesis is persisted as it is created, and the iteration status is kept up to date.
    /// </summary>
    /// <param name="runStartedLogLine">If set, appended as the first log line on the first new hypothesis in this run (e.g. worker dequeue).</param>
    Task RunIterationAsync(
        ResearchIterationId iterationId,
        CancellationToken cancellationToken = default,
        string? runStartedLogLine = null);

    /// <summary>
    /// Appends a log line to the most recently created hypothesis for the iteration (by id), if any.
    /// Used when iteration-level processing fails after hypotheses may exist.
    /// </summary>
    Task AppendLogToLatestHypothesisInIterationAsync(
        ResearchIterationId iterationId,
        string message,
        string? source = null,
        CancellationToken cancellationToken = default);
}
