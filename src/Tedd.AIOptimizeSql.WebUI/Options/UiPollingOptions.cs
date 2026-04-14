namespace Tedd.AIOptimizeSql.WebUI.Options;

public sealed class UiPollingOptions
{
    public const string SectionName = "UiPolling";

    /// <summary>
    /// Background refresh interval for AI connections, database connections, and experiments list pages.
    /// </summary>
    public int EntityListRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// Background refresh for research iterations, hypotheses under an iteration, experiment results view, and benchmark detail.
    /// </summary>
    public int ResearchFlowRefreshSeconds { get; set; } = 2;
}
