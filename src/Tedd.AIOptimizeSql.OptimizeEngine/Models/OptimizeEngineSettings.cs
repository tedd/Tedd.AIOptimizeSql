namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

public sealed class OptimizeEngineSettings
{
    public int QueuePollIntervalSeconds { get; set; } = 10;
    public int BatchStateCheckIntervalSeconds { get; set; } = 5;
    public int MaxToolResponseBytes { get; set; } = 524_288;

    /// <summary>Timed benchmark iterations per hypothesis (after cache clearing each time).</summary>
    public int BenchmarkIterations { get; set; } = 5;

    /// <summary>Pre-measurement warm-up iterations (timings discarded).</summary>
    public int WarmUpIterations { get; set; } = 1;

    /// <summary>Max retries when AI-generated optimize or revert SQL fails to execute.</summary>
    public int AiMaxRetries { get; set; } = 3;

    /// <summary>Milliseconds to pause after cache clearing before each timed measurement.</summary>
    public int PostClearStabilizationMs { get; set; } = 1500;

    /// <summary>Interval for polling queued database analyses.</summary>
    public int AnalysisPollIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of agent runs (initial run + continuations) per job. The agent
    /// maintains a task list; after each run it is asked to continue while Pending or
    /// InProgress tasks remain, up to this limit.
    /// </summary>
    public int MaxAgentContinuations { get; set; } = 20;

    /// <summary>Web search configuration for AI agents (Brave Search API).</summary>
    public WebSearchSettings WebSearch { get; set; } = new();
}

/// <summary>
/// Configuration for the AI web search harness. Configure under
/// <c>OptimizeEngine:WebSearch</c>. Web search tools are only exposed to the
/// AI when an API key is present.
/// </summary>
public sealed class WebSearchSettings
{
    /// <summary>Search provider. Currently only "Brave" is supported.</summary>
    public string Provider { get; set; } = "Brave";

    /// <summary>Brave Search API subscription token (https://brave.com/search/api/).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Search endpoint URL.</summary>
    public string Endpoint { get; set; } = "https://api.search.brave.com/res/v1/web/search";

    /// <summary>Maximum search results returned per query.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>HTTP timeout for search and page fetches.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum bytes of extracted page text returned by the fetch tool.</summary>
    public int MaxPageBytes { get; set; } = 131_072;

    /// <summary>True when an API key is configured and web search tools can be offered.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
