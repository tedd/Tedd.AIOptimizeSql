using System.ComponentModel;
using System.Data.Common;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// Primitive schema inspection tools exposed to the AI optimization agent
/// via <c>AIFunctionFactory.Create</c>. These are fallback tools for
/// selective follow-up inspection when the composite discovery result
/// needs supplementing.
/// </summary>
public sealed class SchemaInspectionToolWrapper : IDisposable
{
    private readonly IDatabaseExecutor _executor;
    private readonly DbConnection _connection;
    private readonly int _maxResponseBytes;
    private readonly ILogger _logger;

    public SchemaInspectionToolWrapper(
        IDatabaseExecutor executor, DbConnection connection,
        int maxResponseBytes, ILogger logger)
    {
        _executor = executor;
        _connection = connection;
        _maxResponseBytes = maxResponseBytes;
        _logger = logger;
    }

    [Description("Gets the full source code (CREATE statement) of a view, stored procedure, or function.")]
    public string GetObjectDefinition(
        [Description("Schema name (e.g. 'dbo')")] string schemaName,
        [Description("Object name (e.g. 'MyView')")] string objectName)
    {
        _logger.LogDebug("AI tool: GetObjectDefinition({Schema}.{Object})", schemaName, objectName);
        try
        {
            var sql = $"SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME('{Escape(schemaName)}') + '.' + QUOTENAME('{Escape(objectName)}')))";
            var result = _executor.ExecuteScalar(_connection, sql);
            return string.IsNullOrWhiteSpace(result) ? "(definition not available - object may be encrypted or not found)" : Truncate(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetObjectDefinition failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Lists objects that a given object references and objects that reference it, using sys.sql_expression_dependencies.")]
    public string GetObjectDependencies(
        [Description("Schema name")] string schemaName,
        [Description("Object name")] string objectName)
    {
        _logger.LogDebug("AI tool: GetObjectDependencies({Schema}.{Object})", schemaName, objectName);
        try
        {
            var sql = $"""
                SELECT 'references' AS direction, 
                       ISNULL(SCHEMA_NAME(d.referenced_schema_id),'dbo') AS dep_schema,
                       d.referenced_entity_name AS dep_name
                FROM sys.sql_expression_dependencies d
                JOIN sys.objects o ON o.object_id = d.referencing_id
                WHERE SCHEMA_NAME(o.schema_id) = '{Escape(schemaName)}' AND o.name = '{Escape(objectName)}'
                UNION ALL
                SELECT 'referenced_by', SCHEMA_NAME(o.schema_id), o.name
                FROM sys.sql_expression_dependencies d
                JOIN sys.objects o ON o.object_id = d.referencing_id
                WHERE d.referenced_id = OBJECT_ID(QUOTENAME('{Escape(schemaName)}') + '.' + QUOTENAME('{Escape(objectName)}'))
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetObjectDependencies failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Gets the parameters of a stored procedure or function, including data types and directions.")]
    public string GetObjectParameters(
        [Description("Schema name")] string schemaName,
        [Description("Object name")] string objectName)
    {
        _logger.LogDebug("AI tool: GetObjectParameters({Schema}.{Object})", schemaName, objectName);
        try
        {
            var sql = $"""
                SELECT p.name, TYPE_NAME(p.user_type_id) AS data_type,
                       p.max_length, p.precision, p.scale, p.is_output
                FROM sys.parameters p
                WHERE p.object_id = OBJECT_ID(QUOTENAME('{Escape(schemaName)}') + '.' + QUOTENAME('{Escape(objectName)}'))
                  AND p.parameter_id > 0
                ORDER BY p.parameter_id
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetObjectParameters failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Gets the column definitions for a table or view, including data types, nullability, identity, and computed flags.")]
    public string GetObjectColumns(
        [Description("Schema name")] string schemaName,
        [Description("Object name (table or view)")] string objectName)
    {
        _logger.LogDebug("AI tool: GetObjectColumns({Schema}.{Object})", schemaName, objectName);
        try
        {
            var sql = $"""
                SELECT c.name, TYPE_NAME(c.user_type_id) AS data_type,
                       c.max_length, c.precision, c.scale,
                       c.is_nullable, c.is_identity, c.is_computed
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID(QUOTENAME('{Escape(schemaName)}') + '.' + QUOTENAME('{Escape(objectName)}'))
                ORDER BY c.column_id
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetObjectColumns failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Gets all indexes on a table with key columns, included columns, filter definitions, uniqueness, and compression type.")]
    public string GetTableIndexes(
        [Description("Schema name")] string schemaName,
        [Description("Table name")] string tableName)
    {
        _logger.LogDebug("AI tool: GetTableIndexes({Schema}.{Table})", schemaName, tableName);
        try
        {
            var sql = $"""
                SELECT i.name AS index_name, i.type_desc, i.is_unique, i.is_primary_key,
                       i.filter_definition, p.data_compression_desc,
                       c.name AS column_name, ic.is_included_column, ic.key_ordinal
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                LEFT JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
                WHERE i.object_id = OBJECT_ID(QUOTENAME('{Escape(schemaName)}') + '.' + QUOTENAME('{Escape(tableName)}'))
                  AND i.name IS NOT NULL
                ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetTableIndexes failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Gets storage information for a table: compression, partitioning, row/page counts, memory-optimized and temporal flags.")]
    public string GetTableStorage(
        [Description("Schema name")] string schemaName,
        [Description("Table name")] string tableName)
    {
        _logger.LogDebug("AI tool: GetTableStorage({Schema}.{Table})", schemaName, tableName);
        try
        {
            var sql = $"""
                SELECT
                    SUM(ps.row_count) AS row_count,
                    SUM(ps.used_page_count) AS page_count,
                    MAX(p.data_compression_desc) AS compression,
                    t.is_memory_optimized, t.temporal_type,
                    CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = t.object_id AND i.type IN (5,6))
                         THEN 1 ELSE 0 END AS has_columnstore,
                    pf.name AS partition_function
                FROM sys.tables t
                LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id AND ps.index_id IN (0,1)
                LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1) AND p.partition_number = 1
                LEFT JOIN sys.indexes ci ON ci.object_id = t.object_id AND ci.index_id IN (0,1)
                LEFT JOIN sys.partition_schemes psch ON psch.data_space_id = ci.data_space_id
                LEFT JOIN sys.partition_functions pf ON pf.function_id = psch.function_id
                WHERE t.schema_id = SCHEMA_ID('{Escape(schemaName)}') AND t.name = '{Escape(tableName)}'
                GROUP BY t.object_id, t.is_memory_optimized, t.temporal_type, pf.name
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetTableStorage failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Lists enabled triggers on a table with their type (AFTER/INSTEAD OF) and triggering events.")]
    public string GetTriggerInfo(
        [Description("Schema name")] string schemaName,
        [Description("Parent table name")] string parentObject)
    {
        _logger.LogDebug("AI tool: GetTriggerInfo({Schema}.{Object})", schemaName, parentObject);
        try
        {
            var sql = $"""
                SELECT tr.name, tr.type_desc,
                       CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS timing,
                       STUFF((
                           SELECT ', ' + te.type_desc
                           FROM sys.trigger_events te WHERE te.object_id = tr.object_id
                           FOR XML PATH('')
                       ), 1, 2, '') AS events
                FROM sys.triggers tr
                JOIN sys.objects o ON o.object_id = tr.parent_id
                WHERE SCHEMA_NAME(o.schema_id) = '{Escape(schemaName)}' AND o.name = '{Escape(parentObject)}'
                  AND tr.is_disabled = 0
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetTriggerInfo failed");
            return $"ERROR: {ex.Message}";
        }
    }

    [Description("Resolves a synonym to its target object, showing the base_object_name.")]
    public string GetSynonymTarget(
        [Description("Schema name")] string schemaName,
        [Description("Synonym name")] string synonymName)
    {
        _logger.LogDebug("AI tool: GetSynonymTarget({Schema}.{Synonym})", schemaName, synonymName);
        try
        {
            var sql = $"""
                SELECT name, base_object_name
                FROM sys.synonyms
                WHERE schema_id = SCHEMA_ID('{Escape(schemaName)}') AND name = '{Escape(synonymName)}'
                """;
            return FormatQueryResult(sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: GetSynonymTarget failed");
            return $"ERROR: {ex.Message}";
        }
    }

    private string FormatQueryResult(string sql)
    {
        var rows = _executor.ExecuteQuery(_connection, sql);
        if (rows.Count == 0)
            return "(no results)";

        var sb = new StringBuilder();
        var columns = rows[0].Keys.ToList();
        sb.AppendLine(string.Join("\t", columns));
        sb.AppendLine(new string('-', columns.Count * 20));

        foreach (var row in rows)
        {
            if (sb.Length >= _maxResponseBytes)
            {
                sb.AppendLine($"\n... truncated at {_maxResponseBytes} bytes ({rows.Count} total rows) ...");
                break;
            }
            sb.AppendLine(string.Join("\t", columns.Select(c => row.GetValueOrDefault(c, "NULL"))));
        }

        return Truncate(sb.ToString());
    }

    private string Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _maxResponseBytes)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var truncated = Encoding.UTF8.GetString(bytes, 0, _maxResponseBytes);
        return truncated + $"\n... truncated at {_maxResponseBytes} bytes ...";
    }

    private static string Escape(string value) => value.Replace("'", "''");

    public void Dispose() { }
}
