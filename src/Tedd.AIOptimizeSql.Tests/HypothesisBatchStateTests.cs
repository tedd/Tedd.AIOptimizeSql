using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class HypothesisBatchStateTests
{
    public static TheoryData<HypothesisBatchState, int> ExpectedValues =>
        new()
        {
            { HypothesisBatchState.Stopped, 0 },
            { HypothesisBatchState.Queued, 1 },
            { HypothesisBatchState.Paused, 2 },
            { HypothesisBatchState.Running, 3 },
        };

    [Theory]
    [MemberData(nameof(ExpectedValues))]
    public void Enum_has_expected_numeric_values(HypothesisBatchState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    [Fact]
    public void All_members_are_accounted_for()
    {
        var defined = Enum.GetValues<HypothesisBatchState>();
        Assert.Equal(4, defined.Length);
    }
}
