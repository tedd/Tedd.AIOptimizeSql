using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class HypothesisStateTests
{
    public static TheoryData<HypothesisState, int> ExpectedValues =>
        new()
        {
            { HypothesisState.Pending, 0 },
            { HypothesisState.Generating, 1 },
            { HypothesisState.Applying, 2 },
            { HypothesisState.Benchmarking, 3 },
            { HypothesisState.Reverting, 4 },
            { HypothesisState.Completed, 5 },
            { HypothesisState.Generated, 6 },
            { HypothesisState.Failed, 7 },
        };

    [Theory]
    [MemberData(nameof(ExpectedValues))]
    public void Enum_has_expected_numeric_values(HypothesisState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    [Fact]
    public void All_members_are_accounted_for()
    {
        var defined = Enum.GetValues<HypothesisState>();
        Assert.Equal(8, defined.Length);
    }
}
