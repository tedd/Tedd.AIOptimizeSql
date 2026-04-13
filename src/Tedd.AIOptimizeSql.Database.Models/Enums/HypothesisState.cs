namespace Tedd.AIOptimizeSql.Database.Models.Enums;

public enum HypothesisState
{
    Pending,
    Generating,
    Applying,
    Benchmarking,
    Reverting,
    Completed,
    Generated,
    Failed
}
