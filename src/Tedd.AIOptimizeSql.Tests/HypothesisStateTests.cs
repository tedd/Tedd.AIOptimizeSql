using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class HypothesisStateTests
{
    public static TheoryData<HypothesisState, int> ExpectedValues =>
        new()
        {
            { HypothesisState.Pending, 0 },
            { HypothesisState.Generating, 1 },
            { HypothesisState.Generated, 2 },
            { HypothesisState.Failed, 3 },
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
        Assert.Equal(4, defined.Length);
    }
}
