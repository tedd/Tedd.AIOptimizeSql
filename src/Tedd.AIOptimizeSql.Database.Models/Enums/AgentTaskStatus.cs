namespace Tedd.AIOptimizeSql.Database.Models.Enums;

public enum AgentTaskStatus
{
    Pending,
    InProgress,
    Completed,
    /// <summary>Removed from the plan (superseded or no longer relevant). Does not block completion.</summary>
    Cancelled
}
