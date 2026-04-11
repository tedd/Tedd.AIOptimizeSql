using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum BenchmarkRunId { }

public record BenchmarkRun
{
    [Key]
    public BenchmarkRunId Id { get; set; }

    /// <summary>
    /// Total time for this benchmark call.
    /// </summary>
    public required int TotalTimeMs { get; set; }
    /// <summary>
    /// Server reported CPU time.
    /// </summary>
    public required int TotalServerCpuTimeMs { get; set; }
    /// <summary>
    /// Server reported elapsed time.
    /// </summary>
    public required int TotalServerElapsedTimeMs { get; set; }

    public int TotalScanCount { get; set; }
    public int TotalLogicalReads { get; set; }
    public int TotalPhysicalReads { get; set; }
    public int TotalPageServerReads { get; set; }
    public int TotalReadAheadReads { get; set; }
    public int TotalPageServerReadAheadReads { get; set; }
    public int TotalLobLogicalReads { get; set; }
    public int TotalLobPhysicalReads { get; set; }
    public int TotalLobPageServerReads { get; set; }
    public int TotalLobReadAheadReads { get; set; }
    public int TotalLobPageServerReadAheadReads { get; set; }

    public List<string> ActualPlanXml { get; set; } = new();
    /// <summary>
    /// Messages returned from Sql server
    /// </summary>
    public string? Messages { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}