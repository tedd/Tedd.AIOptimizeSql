namespace Tedd.AIOptimizeSql.Database;

public enum AIOptimizeDatabaseProvider
{
    SqlServer,
    Sqlite
}

/// <summary>
/// Resolved database settings for the AIOptimize metadata store, registered as a
/// singleton by <see cref="AIOptimizeDatabaseServiceExtensions.AddAIOptimizeDatabase"/>
/// so services can branch on the active provider without re-parsing configuration.
/// </summary>
public sealed record AIOptimizeDatabaseOptions(
    AIOptimizeDatabaseProvider Provider,
    string ConnectionString);
