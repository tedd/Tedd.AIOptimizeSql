using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Runs user-authored SQL from the query window. Never throws for SQL-level failures —
/// server errors come back on <see cref="AdHocQueryResult.ErrorMessage"/> so the UI can
/// show them in the Messages tab the way an admin tool does.
/// </summary>
public interface IAdHocQueryService
{
    Task<AdHocQueryResult> ExecuteAsync(
        string connectionString, AdHocQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Opens and immediately closes a connection, returning the server and database it
    /// landed on. Used by the browser to confirm a connection before listing objects.
    /// </summary>
    Task<(bool Ok, string? ServerName, string? DatabaseName, string? Error)> TestConnectionAsync(
        string connectionString, CancellationToken ct = default);
}
