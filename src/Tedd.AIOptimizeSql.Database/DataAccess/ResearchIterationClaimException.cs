using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

/// <summary>
/// Thrown when the head of the run queue was identified but could not be transitioned into a
/// run. Carries the iteration id so the caller can report the failure on the iteration itself
/// instead of leaving it sitting in <see cref="Models.Enums.ResearchIterationState.Queued"/>
/// with no explanation.
/// </summary>
public sealed class ResearchIterationClaimException(ResearchIterationId iterationId, Exception innerException)
    : Exception($"Failed to claim research iteration {iterationId} from the run queue.", innerException)
{
    public ResearchIterationId IterationId { get; } = iterationId;
}
