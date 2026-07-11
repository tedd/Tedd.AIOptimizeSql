namespace Tedd.AIOptimizeSql.Database.Models.Enums;

public enum DatabaseAnalysisState
{
    /// <summary>Created but not yet queued for processing.</summary>
    Draft,
    /// <summary>Waiting for the engine to pick it up.</summary>
    Queued,
    /// <summary>Currently being processed.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Completed,
    /// <summary>Aborted with an error (see LastMessage).</summary>
    Failed,
    /// <summary>Stopped by the user.</summary>
    Stopped
}
