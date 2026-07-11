namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

/// <summary>
/// Deterministic collection of SQL Server performance metrics gathered at the
/// start of a database analysis. Sections are keyed by collector name and hold
/// raw rows (column → string value) exactly as returned by the DMV queries.
/// </summary>
public sealed class PerformanceSnapshot
{
    public DateTime CollectedAtUtc { get; set; } = DateTime.UtcNow;

    public string? DatabaseName { get; set; }

    public string? ServerVersion { get; set; }

    /// <summary>Collector name → result rows.</summary>
    public Dictionary<string, List<Dictionary<string, string>>> Sections { get; set; } = new();

    /// <summary>Collector name → error message for collectors that failed (e.g. missing DMV permissions).</summary>
    public Dictionary<string, string> Errors { get; set; } = new();
}
