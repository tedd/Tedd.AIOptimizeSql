using Microsoft.Extensions.Logging.Abstractions;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class AiAgentFactoryTests
{
    private static AiAgentFactory Factory => new(NullLoggerFactory.Instance);

    private static AIConnection MinimalConnection(AiProvider provider) =>
        new()
        {
            Name = "test",
            Provider = provider,
            Model = "test-model",
            Endpoint = "https://127.0.0.1:1",
            ApiKey = "dummy-key-for-unit-test",
        };

    [Fact]
    public void Create_throws_NotSupportedException_for_unknown_provider()
    {
        var connection = MinimalConnection((AiProvider)999);

        var ex = Assert.Throws<NotSupportedException>(() =>
            Factory.Create(connection, "instructions", []));

        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AiProvider.AzureOpenAI)]
    [InlineData(AiProvider.OpenAI)]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.Ollama)]
    [InlineData(AiProvider.Local)]
    public void Create_returns_agent_for_supported_providers(AiProvider provider)
    {
        var connection = MinimalConnection(provider);

        var agent = Factory.Create(connection, "Be brief.", []);

        Assert.NotNull(agent);
    }
}
