using System.ComponentModel.DataAnnotations;

namespace Tedd.AIOptimizeSql.Database.Models;

public enum RunQueueId { }

public record RunQueue
{
    [Key]
    public RunQueueId Id { get; set; }
    public ResearchIterationId ResearchIterationId { get; set; }
    public ResearchIteration? ResearchIteration { get; set; }

    /// <summary>
    /// Created UTC
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated when the queue row is created or changed.
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}

