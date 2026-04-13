using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Deterministic schema discovery that traverses SQL Server catalog metadata
/// to build a normalized object graph. Does not use AI -- catalog views
/// (<c>sys.sql_expression_dependencies</c>, <c>sys.sql_modules</c>, etc.)
/// are the sole source of truth.
/// </summary>
public sealed partial class SchemaDiscoveryService(ILogger<SchemaDiscoveryService> logger)
    : ISchemaDiscoveryService
{
    private const int CommandTimeout = 120;

    public async Task<SchemaDiscoveryResult> DiscoverSqlContextAsync(
        string benchmarkSql,
        DbConnection connection,
        CancellationToken ct = default)
    {
        logger.LogInformation("Starting schema discovery for benchmark SQL ({Length} chars)", benchmarkSql.Length);

        var allObjects = new Dictionary<string, DiscoveredObject>(StringComparer.OrdinalIgnoreCase);
        var allEdges = new List<DependencyEdge>();
        var warnings = new List<DiscoveryWarning>();

        var rootRefs = await ResolveSqlReferencesAsync(benchmarkSql, connection, ct);

        foreach (var unresolved in rootRefs.Where(r => !r.Resolved))
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = unresolved.OriginalText,
                Message = "Could not resolve object reference in benchmark SQL"
            });
        }

        foreach (var resolved in rootRefs.Where(r => r.Resolved))
        {
            await DiscoverObjectGraphAsync(
                resolved.Schema!, resolved.Name!, connection,
                allObjects, allEdges, warnings, maxDepth: 10, currentDepth: 0, ct);
        }

        // Discover triggers on all discovered tables
        var tables = allObjects.Values
            .Where(o => o.Kind == SqlObjectKind.Table)
            .ToList();

        foreach (var table in tables)
        {
            await DiscoverTriggersAsync(table.Schema, table.Name, connection,
                allObjects, allEdges, warnings, ct);
        }

        // Identify base tables deterministically from the graph
        var baseTables = allObjects.Values
            .Where(o => o.Kind == SqlObjectKind.Table)
            .ToList();

        var baseTableInfos = await SummarizeTablePhysicalDesignAsync(
            baseTables.Select(t => (t.Schema, t.Name)), connection, ct);

        var markdown = BuildMarkdownSummary(allObjects.Values, allEdges, baseTableInfos, warnings);

        var result = new SchemaDiscoveryResult
        {
            Objects = allObjects.Values.ToList(),
            Dependencies = allEdges,
            BaseTables = baseTableInfos,
            Warnings = warnings,
            MarkdownSummary = markdown
        };

        logger.LogInformation(
            "Schema discovery complete: {Objects} objects, {Edges} edges, {Tables} base tables, {Warnings} warnings",
            result.Objects.Count, result.Dependencies.Count, result.BaseTables.Count, result.Warnings.Count);

        return result;
    }

    #region Reference Resolution

    public async Task<IReadOnlyList<SqlReferenceResolution>> ResolveSqlReferencesAsync(
        string sqlText, DbConnection connection, CancellationToken ct)
    {
        var candidates = ExtractObjectCandidates(sqlText);
        var results = new List<SqlReferenceResolution>();

        foreach (var candidate in candidates)
        {
            var (schema, name) = ParseTwoPartName(candidate);

            var resolved = await TryResolveObjectAsync(schema, name, connection, ct);
            if (resolved != null)
            {
                results.Add(new SqlReferenceResolution
                {
                    OriginalText = candidate,
                    Schema = resolved.Value.Schema,
                    Name = resolved.Value.Name,
                    Resolved = true
                });
            }
            else
            {
                results.Add(new SqlReferenceResolution
                {
                    OriginalText = candidate,
                    Resolved = false
                });
            }
        }

        return results.DistinctBy(r => $"{r.Schema}.{r.Name}".ToUpperInvariant()).ToList();
    }

    private static HashSet<string> ExtractObjectCandidates(string sql)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match [schema].[object], schema.object, [object], and bare identifiers
        // after FROM, JOIN, INTO, UPDATE, EXEC, EXECUTE, MERGE
        var pattern = ObjectReferencePattern();
        foreach (Match m in pattern.Matches(sql))
        {
            var name = m.Groups["obj"].Value;
            if (!string.IsNullOrWhiteSpace(name) && !IsSqlKeyword(name))
                candidates.Add(name);
        }

        return candidates;
    }

    private static (string? Schema, string Name) ParseTwoPartName(string name)
    {
        name = name.Replace("[", "").Replace("]", "");
        var parts = name.Split('.', 2);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (null, parts[0]);
    }

    private static async Task<(string Schema, string Name)?> TryResolveObjectAsync(
        string? schema, string name, DbConnection conn, CancellationToken ct)
    {
        var sql = schema != null
            ? "SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS [Name] FROM sys.objects WHERE SCHEMA_NAME(schema_id) = @schema AND name = @name AND type NOT IN ('S','IT','PK','UQ','D','F','C')"
            : "SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS [Name] FROM sys.objects WHERE name = @name AND type NOT IN ('S','IT','PK','UQ','D','F','C')";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@name", name);
        if (schema != null)
            AddParam(cmd, "@schema", schema);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetString(0), reader.GetString(1));

        return null;
    }

    #endregion

    #region Object Graph Traversal

    private async Task DiscoverObjectGraphAsync(
        string schema, string objectName, DbConnection conn,
        Dictionary<string, DiscoveredObject> objects,
        List<DependencyEdge> edges,
        List<DiscoveryWarning> warnings,
        int maxDepth, int currentDepth,
        CancellationToken ct)
    {
        var key = $"{schema}.{objectName}".ToUpperInvariant();
        if (objects.ContainsKey(key) || currentDepth > maxDepth)
            return;

        var obj = await LoadObjectMetadataAsync(schema, objectName, conn, warnings, ct);
        if (obj == null)
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{objectName}",
                Message = "Object not found in sys.objects"
            });
            return;
        }

        objects[key] = obj;

        // If it's a synonym, resolve target and discover that
        if (obj.Kind == SqlObjectKind.Synonym)
        {
            var target = await ResolveSynonymTargetAsync(schema, objectName, conn, ct);
            if (target != null)
            {
                edges.Add(new DependencyEdge
                {
                    ReferencingSchema = schema,
                    ReferencingName = objectName,
                    ReferencedSchema = target.Value.Schema,
                    ReferencedName = target.Value.Name,
                    IsSchemabound = false
                });
                await DiscoverObjectGraphAsync(
                    target.Value.Schema, target.Value.Name, conn,
                    objects, edges, warnings, maxDepth, currentDepth + 1, ct);
            }
            else
            {
                warnings.Add(new DiscoveryWarning
                {
                    ObjectName = $"{schema}.{objectName}",
                    Message = "Synonym target could not be resolved"
                });
            }
            return;
        }

        // Load dependencies from sys.sql_expression_dependencies
        var deps = await LoadDependenciesAsync(schema, objectName, conn, ct);
        foreach (var dep in deps)
        {
            edges.Add(new DependencyEdge
            {
                ReferencingSchema = schema,
                ReferencingName = objectName,
                ReferencedSchema = dep.ReferencedSchema,
                ReferencedName = dep.ReferencedName,
                ReferencedColumnName = dep.ReferencedColumnName,
                IsSchemabound = dep.IsSchemabound
            });

            if (dep.ReferencedDatabase != null)
            {
                warnings.Add(new DiscoveryWarning
                {
                    ObjectName = $"{dep.ReferencedSchema}.{dep.ReferencedName}",
                    Message = $"Cross-database reference to [{dep.ReferencedDatabase}] - cannot resolve transitively"
                });
                continue;
            }

            if (dep.IsAmbiguous)
            {
                warnings.Add(new DiscoveryWarning
                {
                    ObjectName = $"{dep.ReferencedSchema}.{dep.ReferencedName}",
                    Message = "Ambiguous dependency - metadata may be incomplete"
                });
            }

            await DiscoverObjectGraphAsync(
                dep.ReferencedSchema, dep.ReferencedName, conn,
                objects, edges, warnings, maxDepth, currentDepth + 1, ct);
        }
    }

    private async Task<DiscoveredObject?> LoadObjectMetadataAsync(
        string schema, string name, DbConnection conn,
        List<DiscoveryWarning> warnings, CancellationToken ct)
    {
        const string sql = """
            SELECT
                o.object_id,
                o.type,
                m.definition,
                m.is_schema_bound,
                CASE WHEN m.definition IS NULL AND o.type IN ('P','V','FN','IF','TF','TR') THEN 1 ELSE 0 END AS is_encrypted,
                ISNULL(t.is_memory_optimized, 0) AS is_memory_optimized,
                ISNULL(t.temporal_type, 0) AS temporal_type,
                CASE WHEN o.type IN ('PC','FS','FT') THEN 1 ELSE 0 END AS is_clr,
                CASE WHEN o.type = 'ET' THEN 1 ELSE 0 END AS is_external
            FROM sys.objects o
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            LEFT JOIN sys.tables t ON t.object_id = o.object_id
            WHERE o.schema_id = SCHEMA_ID(@schema) AND o.name = @name
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var objectId = reader.GetInt32(0);
        var typeCode = reader.GetString(1).Trim();
        var definition = reader.IsDBNull(2) ? null : reader.GetString(2);
        var isSchemaBound = !reader.IsDBNull(3) && reader.GetBoolean(3);
        var isEncrypted = reader.GetInt32(4) == 1;
        var isMemoryOptimized = reader.GetInt32(5) == 1;
        var temporalType = reader.GetInt32(6);
        var isClr = reader.GetInt32(7) == 1;
        var isExternal = reader.GetInt32(8) == 1;

        var kind = MapObjectType(typeCode);
        var hasDynamicSql = definition != null &&
            (definition.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase) ||
             DynamicExecPattern().IsMatch(definition));

        if (isEncrypted)
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{name}",
                Message = "Encrypted module - definition not available"
            });
        }

        if (isClr)
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{name}",
                Message = "CLR module - dependency tracking may be incomplete"
            });
        }

        if (hasDynamicSql)
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{name}",
                Message = "Dynamic SQL detected - transitive dependencies may be incomplete"
            });
        }

        if (!isSchemaBound && kind is SqlObjectKind.View or SqlObjectKind.ScalarFunction
            or SqlObjectKind.TableValuedFunction or SqlObjectKind.InlineTableValuedFunction)
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{name}",
                Message = "Non-schema-bound module - dependency metadata may be stale after renames"
            });
        }

        if (definition != null && SelectStarPattern().IsMatch(definition))
        {
            warnings.Add(new DiscoveryWarning
            {
                ObjectName = $"{schema}.{name}",
                Message = "SELECT * detected - column list may not match current table schema"
            });
        }

        // Load columns for tables/views
        IReadOnlyList<ColumnInfo>? columns = null;
        if (kind is SqlObjectKind.Table or SqlObjectKind.View)
        {
            await reader.CloseAsync();
            columns = await LoadColumnsAsync(schema, name, conn, ct);
        }

        // Load parameters for procs/functions
        IReadOnlyList<ObjectParameterInfo>? parameters = null;
        if (kind is SqlObjectKind.StoredProcedure or SqlObjectKind.ScalarFunction
            or SqlObjectKind.TableValuedFunction or SqlObjectKind.InlineTableValuedFunction)
        {
            if (!reader.IsClosed) await reader.CloseAsync();
            parameters = await LoadParametersAsync(schema, name, conn, ct);
        }

        return new DiscoveredObject
        {
            Schema = schema,
            Name = name,
            Kind = kind,
            ObjectId = objectId,
            Definition = definition,
            Columns = columns,
            Parameters = parameters,
            IsEncrypted = isEncrypted,
            IsSchemaBound = isSchemaBound,
            IsMemoryOptimized = isMemoryOptimized,
            IsTemporal = temporalType != 0,
            IsExternal = isExternal,
            IsClr = isClr,
            HasDynamicSqlLikely = hasDynamicSql
        };
    }

    #endregion

    #region Dependencies

    private record DependencyRow(
        string ReferencedSchema, string ReferencedName,
        string? ReferencedColumnName, string? ReferencedDatabase,
        bool IsSchemabound, bool IsAmbiguous);

    private async Task<List<DependencyRow>> LoadDependenciesAsync(
        string schema, string objectName, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ISNULL(SCHEMA_NAME(d.referenced_schema_id), 'dbo') AS referenced_schema,
                d.referenced_entity_name,
                d.referenced_minor_name,
                d.referenced_database_name,
                d.is_schema_bound_reference,
                d.is_ambiguous
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            WHERE SCHEMA_NAME(o.schema_id) = @schema AND o.name = @name
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", objectName);

        var results = new List<DependencyRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DependencyRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        }

        return results;
    }

    private async Task DiscoverTriggersAsync(
        string tableSchema, string tableName, DbConnection conn,
        Dictionary<string, DiscoveredObject> objects,
        List<DependencyEdge> edges,
        List<DiscoveryWarning> warnings,
        CancellationToken ct)
    {
        const string sql = """
            SELECT SCHEMA_NAME(tr.schema_id) AS trigger_schema, tr.name AS trigger_name
            FROM sys.triggers tr
            JOIN sys.objects o ON o.object_id = tr.parent_id
            WHERE SCHEMA_NAME(o.schema_id) = @schema AND o.name = @table
              AND tr.is_disabled = 0
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", tableSchema);
        AddParam(cmd, "@table", tableName);

        var triggers = new List<(string Schema, string Name)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            triggers.Add((reader.GetString(0), reader.GetString(1)));

        foreach (var (trigSchema, trigName) in triggers)
        {
            edges.Add(new DependencyEdge
            {
                ReferencingSchema = trigSchema,
                ReferencingName = trigName,
                ReferencedSchema = tableSchema,
                ReferencedName = tableName,
                IsSchemabound = false
            });
            await DiscoverObjectGraphAsync(
                trigSchema, trigName, conn, objects, edges, warnings,
                maxDepth: 3, currentDepth: 0, ct);
        }
    }

    private async Task<(string Schema, string Name)?> ResolveSynonymTargetAsync(
        string schema, string name, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT base_object_name
            FROM sys.synonyms
            WHERE schema_id = SCHEMA_ID(@schema) AND name = @name
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        var baseObject = (string?)await cmd.ExecuteScalarAsync(ct);
        if (baseObject == null) return null;

        var parsed = ParseTwoPartName(baseObject);
        return (parsed.Schema ?? "dbo", parsed.Name);
    }

    #endregion

    #region Table Physical Design

    public async Task<IReadOnlyList<BaseTableInfo>> SummarizeTablePhysicalDesignAsync(
        IEnumerable<(string Schema, string Table)> tables,
        DbConnection connection, CancellationToken ct)
    {
        var results = new List<BaseTableInfo>();

        foreach (var (schema, table) in tables)
        {
            var columns = await LoadColumnsAsync(schema, table, connection, ct);
            var indexes = await LoadIndexesAsync(schema, table, connection, ct);
            var foreignKeys = await LoadForeignKeysAsync(schema, table, connection, ct);
            var (rowCount, pageCount, compression, partitionInfo, isMemOpt, temporalType, hasColumnstore) =
                await LoadTableStorageAsync(schema, table, connection, ct);

            results.Add(new BaseTableInfo
            {
                Schema = schema,
                Table = table,
                Columns = columns,
                Indexes = indexes,
                ForeignKeys = foreignKeys,
                CompressionState = compression,
                PartitionInfo = partitionInfo,
                RowCount = rowCount,
                PageCount = pageCount,
                IsMemoryOptimized = isMemOpt,
                IsTemporal = temporalType != 0,
                HasColumnstore = hasColumnstore
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<ColumnInfo>> LoadColumnsAsync(
        string schema, string name, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.name,
                TYPE_NAME(c.user_type_id) AS data_type,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                dc.definition AS default_value
            FROM sys.columns c
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))
            ORDER BY c.column_id
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        var results = new List<ColumnInfo>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                MaxLength = reader.IsDBNull(2) ? null : reader.GetInt16(2),
                Precision = reader.IsDBNull(3) ? null : (int)reader.GetByte(3),
                Scale = reader.IsDBNull(4) ? null : (int)reader.GetByte(4),
                IsNullable = reader.GetBoolean(5),
                IsIdentity = reader.GetBoolean(6),
                IsComputed = reader.GetBoolean(7),
                DefaultValue = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<ObjectParameterInfo>> LoadParametersAsync(
        string schema, string name, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                p.name,
                TYPE_NAME(p.user_type_id) AS data_type,
                p.is_output,
                p.default_value
            FROM sys.parameters p
            WHERE p.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))
              AND p.parameter_id > 0
            ORDER BY p.parameter_id
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        var results = new List<ObjectParameterInfo>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ObjectParameterInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsOutput = reader.GetBoolean(2),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString()
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<IndexInfo>> LoadIndexesAsync(
        string schema, string table, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                i.name,
                i.type_desc,
                i.is_unique,
                i.is_primary_key,
                i.filter_definition,
                p.data_compression_desc,
                ic.column_id,
                c.name AS column_name,
                ic.is_included_column,
                ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            LEFT JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
            WHERE i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
              AND i.name IS NOT NULL
            ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        var indexData = new Dictionary<string, (string Type, bool IsUnique, bool IsPK, string? Filter, string? Compression,
            List<string> Keys, List<string> Included)>(StringComparer.OrdinalIgnoreCase);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idxName = reader.GetString(0);
            var typeDesc = reader.GetString(1);
            var isUnique = reader.GetBoolean(2);
            var isPK = reader.GetBoolean(3);
            var filter = reader.IsDBNull(4) ? null : reader.GetString(4);
            var compression = reader.IsDBNull(5) ? null : reader.GetString(5);
            var colName = reader.GetString(7);
            var isIncluded = reader.GetBoolean(8);

            if (!indexData.TryGetValue(idxName, out var data))
            {
                data = (typeDesc, isUnique, isPK, filter, compression, new List<string>(), new List<string>());
                indexData[idxName] = data;
            }

            if (isIncluded)
                data.Included.Add(colName);
            else
                data.Keys.Add(colName);
        }

        return indexData.Select(kv => new IndexInfo
        {
            Name = kv.Key,
            Type = kv.Value.Type,
            IsUnique = kv.Value.IsUnique,
            IsPrimaryKey = kv.Value.IsPK,
            KeyColumns = kv.Value.Keys,
            IncludedColumns = kv.Value.Included.Count > 0 ? kv.Value.Included : null,
            FilterDefinition = kv.Value.Filter,
            CompressionType = kv.Value.Compression
        }).ToList();
    }

    private async Task<IReadOnlyList<ForeignKeyInfo>> LoadForeignKeysAsync(
        string schema, string table, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                fk.name AS fk_name,
                COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS parent_column,
                SCHEMA_NAME(rt.schema_id) AS ref_schema,
                rt.name AS ref_table,
                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ref_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            WHERE fk.parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        var fkData = new Dictionary<string, (List<string> Cols, string RefSchema, string RefTable, List<string> RefCols)>(
            StringComparer.OrdinalIgnoreCase);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fkName = reader.GetString(0);
            var parentCol = reader.GetString(1);
            var refSchema = reader.GetString(2);
            var refTable = reader.GetString(3);
            var refCol = reader.GetString(4);

            if (!fkData.TryGetValue(fkName, out var data))
            {
                data = (new List<string>(), refSchema, refTable, new List<string>());
                fkData[fkName] = data;
            }

            data.Cols.Add(parentCol);
            data.RefCols.Add(refCol);
        }

        return fkData.Select(kv => new ForeignKeyInfo
        {
            Name = kv.Key,
            Columns = kv.Value.Cols,
            ReferencedSchema = kv.Value.RefSchema,
            ReferencedTable = kv.Value.RefTable,
            ReferencedColumns = kv.Value.RefCols
        }).ToList();
    }

    private async Task<(long RowCount, long PageCount, string? Compression, string? PartitionInfo,
        bool IsMemoryOptimized, int TemporalType, bool HasColumnstore)>
        LoadTableStorageAsync(string schema, string table, DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ISNULL(SUM(ps.row_count), 0) AS row_count,
                ISNULL(SUM(ps.used_page_count), 0) AS page_count,
                MAX(p.data_compression_desc) AS compression,
                t.is_memory_optimized,
                t.temporal_type,
                CASE WHEN EXISTS (
                    SELECT 1 FROM sys.indexes i
                    WHERE i.object_id = t.object_id AND i.type IN (5,6)
                ) THEN 1 ELSE 0 END AS has_columnstore,
                pf.name AS partition_function
            FROM sys.tables t
            LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id AND ps.index_id IN (0,1)
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1) AND p.partition_number = 1
            LEFT JOIN sys.indexes ci ON ci.object_id = t.object_id AND ci.index_id IN (0,1)
            LEFT JOIN sys.partition_schemes psch ON psch.data_space_id = ci.data_space_id
            LEFT JOIN sys.partition_functions pf ON pf.function_id = psch.function_id
            WHERE t.schema_id = SCHEMA_ID(@schema) AND t.name = @table
            GROUP BY t.object_id, t.is_memory_optimized, t.temporal_type, pf.name
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0, null, null, false, 0, false);

        var rowCount = reader.GetInt64(0);
        var pageCount = reader.GetInt64(1);
        var compression = reader.IsDBNull(2) ? null : reader.GetString(2);
        var isMemOpt = reader.GetBoolean(3);
        var temporalType = reader.GetInt32(4);
        var hasColumnstore = reader.GetInt32(5) == 1;
        var partFunc = reader.IsDBNull(6) ? null : reader.GetString(6);
        var partInfo = partFunc != null ? $"Partition function: {partFunc}" : null;

        return (rowCount, pageCount, compression, partInfo, isMemOpt, temporalType, hasColumnstore);
    }

    #endregion

    #region Markdown Summary

    private static string BuildMarkdownSummary(
        IEnumerable<DiscoveredObject> objects,
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyList<BaseTableInfo> baseTables,
        IReadOnlyList<DiscoveryWarning> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Schema Discovery Results");
        sb.AppendLine();

        // Warnings
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in warnings)
                sb.AppendLine($"- **{w.ObjectName}**: {w.Message}");
            sb.AppendLine();
        }

        // Object summary
        sb.AppendLine("## Discovered Objects");
        sb.AppendLine();
        foreach (var group in objects.GroupBy(o => o.Kind).OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key}s");
            sb.AppendLine();
            foreach (var obj in group.OrderBy(o => $"{o.Schema}.{o.Name}"))
            {
                sb.AppendLine($"- `[{obj.Schema}].[{obj.Name}]`");
                var flags = new List<string>();
                if (obj.IsEncrypted) flags.Add("encrypted");
                if (obj.IsSchemaBound) flags.Add("schema-bound");
                if (obj.IsMemoryOptimized) flags.Add("memory-optimized");
                if (obj.IsTemporal) flags.Add("temporal");
                if (obj.IsClr) flags.Add("CLR");
                if (obj.HasDynamicSqlLikely) flags.Add("dynamic SQL");
                if (flags.Count > 0)
                    sb.AppendLine($"  - Flags: {string.Join(", ", flags)}");
            }
            sb.AppendLine();
        }

        // Dependencies
        if (edges.Count > 0)
        {
            sb.AppendLine("## Dependency Edges");
            sb.AppendLine();
            foreach (var edge in edges.DistinctBy(e => $"{e.ReferencingSchema}.{e.ReferencingName}->{e.ReferencedSchema}.{e.ReferencedName}"))
                sb.AppendLine($"- `[{edge.ReferencingSchema}].[{edge.ReferencingName}]` -> `[{edge.ReferencedSchema}].[{edge.ReferencedName}]`");
            sb.AppendLine();
        }

        // Base table physical design
        if (baseTables.Count > 0)
        {
            sb.AppendLine("## Base Table Physical Design");
            sb.AppendLine();
            foreach (var t in baseTables.OrderBy(t => $"{t.Schema}.{t.Table}"))
            {
                sb.AppendLine($"### `[{t.Schema}].[{t.Table}]`");
                sb.AppendLine();
                if (t.RowCount.HasValue) sb.AppendLine($"- Rows: {t.RowCount:N0}");
                if (t.PageCount.HasValue) sb.AppendLine($"- Pages: {t.PageCount:N0}");
                if (t.CompressionState != null) sb.AppendLine($"- Compression: {t.CompressionState}");
                if (t.PartitionInfo != null) sb.AppendLine($"- {t.PartitionInfo}");
                if (t.IsMemoryOptimized) sb.AppendLine("- Memory-optimized: YES");
                if (t.IsTemporal) sb.AppendLine("- Temporal: YES");
                if (t.HasColumnstore) sb.AppendLine("- Has columnstore index");
                sb.AppendLine();

                // Columns
                if (t.Columns is { Count: > 0 })
                {
                    sb.AppendLine("**Columns:**");
                    sb.AppendLine();
                    sb.AppendLine("| Name | Type | Nullable | Identity | Computed |");
                    sb.AppendLine("|------|------|----------|----------|----------|");
                    foreach (var c in t.Columns)
                    {
                        var typeStr = c.MaxLength.HasValue && c.MaxLength != -1
                            ? $"{c.DataType}({c.MaxLength})"
                            : c.Precision.HasValue && c.Scale.HasValue && c.Scale > 0
                                ? $"{c.DataType}({c.Precision},{c.Scale})"
                                : c.DataType;
                        sb.AppendLine($"| {c.Name} | {typeStr} | {(c.IsNullable ? "YES" : "NO")} | {(c.IsIdentity ? "YES" : "")} | {(c.IsComputed ? "YES" : "")} |");
                    }
                    sb.AppendLine();
                }

                // Indexes
                if (t.Indexes is { Count: > 0 })
                {
                    sb.AppendLine("**Indexes:**");
                    sb.AppendLine();
                    foreach (var idx in t.Indexes)
                    {
                        var desc = new StringBuilder($"- **{idx.Name}** ({idx.Type}");
                        if (idx.IsUnique) desc.Append(", UNIQUE");
                        if (idx.IsPrimaryKey) desc.Append(", PK");
                        desc.Append($"): [{string.Join(", ", idx.KeyColumns)}]");
                        if (idx.IncludedColumns is { Count: > 0 })
                            desc.Append($" INCLUDE [{string.Join(", ", idx.IncludedColumns)}]");
                        if (idx.FilterDefinition != null)
                            desc.Append($" WHERE {idx.FilterDefinition}");
                        if (idx.CompressionType != null && idx.CompressionType != "NONE")
                            desc.Append($" ({idx.CompressionType})");
                        sb.AppendLine(desc.ToString());
                    }
                    sb.AppendLine();
                }

                // Foreign keys
                if (t.ForeignKeys is { Count: > 0 })
                {
                    sb.AppendLine("**Foreign Keys:**");
                    sb.AppendLine();
                    foreach (var fk in t.ForeignKeys)
                        sb.AppendLine($"- **{fk.Name}**: [{string.Join(", ", fk.Columns)}] -> `[{fk.ReferencedSchema}].[{fk.ReferencedTable}]` [{string.Join(", ", fk.ReferencedColumns)}]");
                    sb.AppendLine();
                }
            }
        }

        // Module definitions
        var modulesWithDefs = objects
            .Where(o => o.Definition != null && o.Kind != SqlObjectKind.Table)
            .OrderBy(o => $"{o.Schema}.{o.Name}")
            .ToList();

        if (modulesWithDefs.Count > 0)
        {
            sb.AppendLine("## Full Object Definitions");
            sb.AppendLine();
            foreach (var obj in modulesWithDefs)
            {
                sb.AppendLine($"### `[{obj.Schema}].[{obj.Name}]` ({obj.Kind})");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(obj.Definition);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Helpers

    private static SqlObjectKind MapObjectType(string typeCode) => typeCode.Trim() switch
    {
        "U" => SqlObjectKind.Table,
        "V" => SqlObjectKind.View,
        "P" => SqlObjectKind.StoredProcedure,
        "PC" => SqlObjectKind.StoredProcedure,
        "FN" => SqlObjectKind.ScalarFunction,
        "FS" => SqlObjectKind.ScalarFunction,
        "TF" => SqlObjectKind.TableValuedFunction,
        "FT" => SqlObjectKind.TableValuedFunction,
        "IF" => SqlObjectKind.InlineTableValuedFunction,
        "TR" => SqlObjectKind.Trigger,
        "TA" => SqlObjectKind.Trigger,
        "SN" => SqlObjectKind.Synonym,
        "SO" => SqlObjectKind.Sequence,
        "TT" => SqlObjectKind.TableType,
        "ET" => SqlObjectKind.Table,
        _ => SqlObjectKind.Unknown
    };

    private static bool IsSqlKeyword(string name) =>
        _sqlKeywords.Contains(name.ToUpperInvariant());

    private static readonly HashSet<string> _sqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN",
        "INSERT", "UPDATE", "DELETE", "SET", "VALUES", "INTO", "JOIN", "INNER",
        "LEFT", "RIGHT", "OUTER", "CROSS", "ON", "AS", "GROUP", "BY", "ORDER",
        "HAVING", "UNION", "ALL", "DISTINCT", "TOP", "WITH", "CASE", "WHEN",
        "THEN", "ELSE", "END", "NULL", "IS", "LIKE", "EXEC", "EXECUTE",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "DECLARE", "MERGE", "USING",
        "OUTPUT", "OVER", "PARTITION", "ROW_NUMBER", "RANK", "DENSE_RANK",
        "LAG", "LEAD", "COUNT", "SUM", "AVG", "MIN", "MAX", "CAST", "CONVERT",
        "ISNULL", "COALESCE", "TABLE", "CREATE", "ALTER", "DROP", "INDEX",
        "NOLOCK", "READUNCOMMITTED", "ROWLOCK", "TABLOCK", "HOLDLOCK"
    };

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Serializes registered base tables to a JSON string for storage.</summary>
    public static string SerializeBaseTables(IReadOnlyList<BaseTableInfo> baseTables) =>
        JsonSerializer.Serialize(
            baseTables.Select(t => new { t.Schema, t.Table }).ToList(),
            new JsonSerializerOptions { WriteIndented = false });

    [GeneratedRegex(
        @"(?:FROM|JOIN|INTO|UPDATE|EXEC(?:UTE)?|MERGE)\s+(?<obj>(?:\[?[\w]+\]?\.)?(?:\[?[\w]+\]?))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ObjectReferencePattern();

    [GeneratedRegex(@"\bEXEC\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DynamicExecPattern();

    [GeneratedRegex(@"\bSELECT\s+\*\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SelectStarPattern();

    #endregion
}

/// <summary>Result of resolving a SQL text reference against catalog metadata.</summary>
public sealed class SqlReferenceResolution
{
    public required string OriginalText { get; init; }
    public string? Schema { get; init; }
    public string? Name { get; init; }
    public bool Resolved { get; init; }
}
