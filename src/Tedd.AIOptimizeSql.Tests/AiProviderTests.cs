using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class AiProviderTests
{
    public static TheoryData<AiProvider, int> ExpectedValues =>
        new()
        {
            { AiProvider.AzureOpenAI, 0 },
            { AiProvider.OpenAI, 1 },
            { AiProvider.Local, 2 },
            { AiProvider.Anthropic, 3 },
            { AiProvider.Ollama, 4 },
        };

    [Theory]
    [MemberData(nameof(ExpectedValues))]
    public void Enum_has_expected_numeric_values(AiProvider provider, int expected)
    {
        Assert.Equal(expected, (int)provider);
    }

    [Fact]
    public void All_members_are_accounted_for()
    {
        var defined = Enum.GetValues<AiProvider>();
        Assert.Equal(5, defined.Length);
    }
}
