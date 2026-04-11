using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum RunQueueId { }

public record RunQueue
{
    [Key]
    public RunQueueId Id { get; set; }
    public HypothesisBatchId HypothesisBatchId { get; set; }
    public HypothesisBatch? HypothesisBatch { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

