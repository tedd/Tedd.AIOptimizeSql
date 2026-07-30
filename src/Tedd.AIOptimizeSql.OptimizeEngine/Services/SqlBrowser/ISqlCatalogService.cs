using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Reads the target database's catalog for the object browser and turns objects into
/// runnable script snippets. Read-only: safe on analyze-only connections.
/// </summary>
public interface ISqlCatalogService
{
    /// <summary>
    /// Lists every browsable object in the database behind <paramref name="connectionString"/>,
    /// with parameters for procedures and functions and row counts for tables.
    /// </summary>
    Task<CatalogSnapshot> GetCatalogAsync(string connectionString, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored module definition from <c>sys.sql_modules</c>, or null when the object
    /// has none (a table) or is encrypted.
    /// </summary>
    Task<string?> GetObjectDefinitionAsync(
        string connectionString, string schema, string name, CancellationToken ct = default);

    /// <summary>
    /// Reconstructs a <c>CREATE TABLE</c> script — columns, computed columns, defaults, primary
    /// key, unique constraints, indexes, and foreign keys — from catalog metadata.
    /// </summary>
    Task<string> ScriptCreateTableAsync(
        string connectionString, string schema, string table, CancellationToken ct = default);

    /// <summary>
    /// Builds the SQL for a context-menu action against <paramref name="catalogObject"/>.
    /// Actions that need the live catalog (definitions, CREATE TABLE) hit the database;
    /// the rest are built from the object's own metadata.
    /// </summary>
    Task<string> BuildScriptAsync(
        string connectionString,
        CatalogObject catalogObject,
        CatalogScriptAction action,
        CancellationToken ct = default);

    /// <summary>Columns of a table or view, for the wizard and the tree's expandable column list.</summary>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string connectionString, string schema, string name, CancellationToken ct = default);
}
