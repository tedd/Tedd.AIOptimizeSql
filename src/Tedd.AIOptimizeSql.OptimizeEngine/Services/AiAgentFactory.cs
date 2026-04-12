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

    private static AIAgent CreateOpenAI(AIConnection connection, string instructions, IList<AITool> tools)
    {
        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(connection.ApiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(connection.Endpoint) });

        ChatClient chatClient = client.GetChatClient(connection.Model);
        return OpenAIChatClientExtensions.AsAIAgent(chatClient, instructions, tools: tools);
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
