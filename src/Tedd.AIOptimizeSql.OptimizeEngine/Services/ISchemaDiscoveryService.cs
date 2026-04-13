using System.Data.Common;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public interface ISchemaDiscoveryService
{
    /// <summary>
    /// Deterministic schema discovery: parses benchmark SQL, resolves objects,
    /// traverses the dependency graph via catalog metadata, and returns a
    /// structured result with all objects, edges, base tables, and warnings.
    /// </summary>
    Task<SchemaDiscoveryResult> DiscoverSqlContextAsync(
        string benchmarkSql, DbConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Lightweight reference resolution: parses SQL text for candidate object
    /// references and validates each against catalog metadata.
    /// </summary>
    Task<IReadOnlyList<SqlReferenceResolution>> ResolveSqlReferencesAsync(
        string sqlText, DbConnection connection, CancellationToken ct);

    /// <summary>
    /// Collects physical design metadata (columns, indexes, FKs, compression,
    /// partitioning, row/page counts) for a set of tables.
    /// </summary>
    Task<IReadOnlyList<BaseTableInfo>> SummarizeTablePhysicalDesignAsync(
        IEnumerable<(string Schema, string Table)> tables,
        DbConnection connection, CancellationToken ct);
}
