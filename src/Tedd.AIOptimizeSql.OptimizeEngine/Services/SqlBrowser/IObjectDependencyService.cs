using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Builds incoming/outgoing dependency graphs from SQL Server catalog metadata.
/// Read-only, deterministic, no AI.
/// </summary>
public interface IObjectDependencyService
{
    /// <summary>
    /// Graph around one object: <paramref name="incomingDepth"/> hops of things that depend on it
    /// and <paramref name="outgoingDepth"/> hops of things it depends on.
    /// </summary>
    Task<ObjectDependencyGraph> GetGraphForObjectAsync(
        string connectionString,
        string schema,
        string name,
        int incomingDepth = 2,
        int outgoingDepth = 3,
        CancellationToken ct = default);

    /// <summary>
    /// Graph around every object a SQL text references. Unresolvable references land in
    /// <see cref="ObjectDependencyGraph.Warnings"/> rather than being dropped silently.
    /// </summary>
    Task<ObjectDependencyGraph> GetGraphForSqlAsync(
        string connectionString,
        string sql,
        int incomingDepth = 1,
        int outgoingDepth = 3,
        CancellationToken ct = default);
}
