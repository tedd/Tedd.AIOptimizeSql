using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OpenAI.Chat;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Builds an <see cref="AIAgent"/> for the given <see cref="AIConnection"/>
/// configuration, selecting the correct provider client.
/// </summary>
public sealed class AiAgentFactory(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AiAgentFactory>();

    public AIAgent Create(AIConnection connection, string instructions, IList<AITool> tools)
    {
        _logger.LogInformation("Creating AI agent for provider {Provider}, model {Model}, endpoint {Endpoint}",
            connection.Provider, connection.Model, connection.Endpoint);

        return connection.Provider switch
        {
            AiProvider.AzureOpenAI => CreateAzureOpenAI(connection, instructions, tools),
            AiProvider.OpenAI => CreateOpenAI(connection, instructions, tools),
            AiProvider.Anthropic => CreateAnthropic(connection, instructions, tools),
            AiProvider.Ollama => CreateOllama(connection, instructions, tools),
            AiProvider.Local => CreateOllama(connection, instructions, tools),
            _ => throw new NotSupportedException($"AI provider '{connection.Provider}' is not supported.")
        };
    }

    private static AIAgent CreateAzureOpenAI(AIConnection connection, string instructions, IList<AITool> tools)
    {
        var client = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(connection.Endpoint),
            new System.ClientModel.ApiKeyCredential(connection.ApiKey));

        ChatClient chatClient = client.GetChatClient(connection.Model);
        return OpenAIChatClientExtensions.AsAIAgent(chatClient, instructions, tools: tools);
    }

    private AIAgent CreateOpenAI(AIConnection connection, string instructions, IList<AITool> tools)
    {
        var raw = new Uri(connection.Endpoint);
        var baseUri = NormalizeOpenAIBaseEndpoint(raw);
        if (baseUri != raw)
            _logger.LogWarning(
                "OpenAI endpoint normalized from {Raw} to {Normalized}; the client appends /chat/completions to the base URL.",
                raw,
                baseUri);

        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(connection.ApiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = baseUri });

        ChatClient chatClient = client.GetChatClient(connection.Model);
        return OpenAIChatClientExtensions.AsAIAgent(chatClient, instructions, tools: tools);
    }

    /// <summary>
    /// The OpenAI .NET client uses a base URL (default <c>https://api.openai.com/v1</c>) and appends
    /// <c>/chat/completions</c>. If the configured endpoint already ends with <c>/models</c> or the full
    /// chat path, requests incorrectly go to <c>/v1/models/chat/completions</c> and return 404.
    /// </summary>
    internal static Uri NormalizeOpenAIBaseEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        const StringComparison ord = StringComparison.OrdinalIgnoreCase;

        if (path.EndsWith("/chat/completions", ord))
            path = path[..^"/chat/completions".Length].TrimEnd('/');

        if (path.EndsWith("/models", ord))
            path = path[..^"/models".Length].TrimEnd('/');

        if (string.IsNullOrEmpty(path) &&
            string.Equals(builder.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
            path = "/v1";

        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        return builder.Uri;
    }

    private static AIAgent CreateAnthropic(AIConnection connection, string instructions, IList<AITool> tools)
    {
        var client = new Anthropic.AnthropicClient { ApiKey = connection.ApiKey };
        return Anthropic.AnthropicClientExtensions.AsAIAgent(
            client,
            model: connection.Model,
            instructions: instructions,
            tools: tools);
    }

    private static AIAgent CreateOllama(AIConnection connection, string instructions, IList<AITool> tools)
    {
        IChatClient chatClient = new OllamaSharp.OllamaApiClient(
            new Uri(connection.Endpoint),
            connection.Model);

        return chatClient.AsAIAgent(instructions: instructions, tools: tools);
    }
}
