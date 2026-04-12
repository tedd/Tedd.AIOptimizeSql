using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class ResearchIterationStateTests
{
    public static TheoryData<ResearchIterationState, int> ExpectedValues =>
        new()
        {
            { ResearchIterationState.Stopped, 0 },
            { ResearchIterationState.Queued, 1 },
            { ResearchIterationState.Paused, 2 },
            { ResearchIterationState.Running, 3 },
        };

    [Theory]
    [MemberData(nameof(ExpectedValues))]
    public void Enum_has_expected_numeric_values(ResearchIterationState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    [Fact]
    public void All_members_are_accounted_for()
    {
        var defined = Enum.GetValues<ResearchIterationState>();
        Assert.Equal(4, defined.Length);
    }
}
