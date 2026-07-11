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

    [Theory]
    [InlineData("https://api.openai.com/v1/models", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/models/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("https://gateway.example/v1/models", "https://gateway.example/v1")]
    public void NormalizeOpenAIBaseEndpoint_fixes_common_misconfigured_bases(string input, string expected)
    {
        var normalized = AiAgentFactory.NormalizeOpenAIBaseEndpoint(new Uri(input));
        Assert.Equal(new Uri(expected), normalized);
    }

    [Theory]
    // Azure portal's next-generation "v1" endpoint (meant for plain OpenAI clients)
    [InlineData("https://myres.openai.azure.com/openai/v1", "https://myres.openai.azure.com/")]
    [InlineData("https://myres.openai.azure.com/openai/v1/", "https://myres.openai.azure.com/")]
    // Full deployment/chat URL pasted from portal or docs
    [InlineData("https://myres.openai.azure.com/openai/deployments/gpt-5/chat/completions?api-version=2024-10-21", "https://myres.openai.azure.com/")]
    // Bare /openai path
    [InlineData("https://myres.openai.azure.com/openai", "https://myres.openai.azure.com/")]
    // Already correct resource root stays unchanged
    [InlineData("https://myres.openai.azure.com", "https://myres.openai.azure.com/")]
    [InlineData("https://myres.openai.azure.com/", "https://myres.openai.azure.com/")]
    public void NormalizeAzureOpenAIEndpoint_strips_paths_to_resource_root(string input, string expected)
    {
        var normalized = AiAgentFactory.NormalizeAzureOpenAIEndpoint(new Uri(input));
        Assert.Equal(new Uri(expected), normalized);
    }
}
