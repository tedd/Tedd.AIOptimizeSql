using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public interface IAiHypothesisService
{
    /// <summary>
    /// Uses an AI agent (with SQL tool access) to generate a new optimisation hypothesis.
    /// </summary>
    /// <param name="batch">
    /// The batch being processed. Must have <see cref="HypothesisBatch.Experiment"/>,
    /// <see cref="Experiment.AIConnection"/>, and <see cref="Experiment.DatabaseConnection"/> loaded.
    /// </param>
    /// <param name="priorHypotheses">Previously generated hypotheses for AI context.</param>
    /// <param name="cancellationToken"/>
    /// <returns>A newly created (but not yet persisted) <see cref="Hypothesis"/>.</returns>
    Task<Hypothesis> GenerateHypothesisAsync(
        HypothesisBatch batch,
        IReadOnlyList<Hypothesis> priorHypotheses,
        CancellationToken cancellationToken = default);
}
