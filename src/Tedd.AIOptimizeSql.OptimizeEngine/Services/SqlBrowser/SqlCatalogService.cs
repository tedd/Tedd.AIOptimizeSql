using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Reads SQL Server catalog views to feed the object browser and to turn a browsed object into
/// runnable script text. Every statement issued here is a <c>SELECT</c> against <c>sys.*</c>,
/// so the service is safe on connections flagged analyze-only.
/// </summary>
public sealed partial class SqlCatalogService(ILogger<SqlCatalogService> logger) : ISqlCatalogService
{
    private const int CommandTimeout = 120;

    /// <summary>Generated SQL uses <c>\n</c> so a script looks identical whoever produced it.</summary>
    private const string Nl = "\n";

    #region Catalog

    public async Task<CatalogSnapshot> GetCatalogAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(connectionString, ct);

        var (serverName, databaseName) = await ReadServerAndDatabaseAsync(conn, ct);
        var rows = await ReadObjectRowsAsync(conn, ct);
        var rowCounts = await ReadRowCountsAsync(conn, ct);
        var parameters = await ReadParametersAsync(conn, ct);

        var objects = new List<CatalogObject>(rows.Count);
        foreach (var row in rows)
        {
            var category = MapCategory(row.TypeCode);
            if (category is null)
                continue;

            long? rowCount = null;
            if (category == CatalogObjectCategory.Table && rowCounts.TryGetValue(row.ObjectId, out var rc))
                rowCount = rc;

            if (!parameters.TryGetValue(row.ObjectId, out var objectParameters))
                objectParameters = [];

            objects.Add(new CatalogObject
            {
                Schema = row.Schema,
                Name = row.Name,
                Category = category.Value,
                TypeCode = row.TypeCode,
                ObjectId = row.ObjectId,
                ModifiedAt = row.ModifiedAt,
                RowCount = rowCount,
                Parameters = objectParameters
            });
        }

        objects.Sort(static (a, b) =>
        {
            var byCategory = ((int)a.Category).CompareTo((int)b.Category);
            if (byCategory != 0) return byCategory;
            var bySchema = string.Compare(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase);
            return bySchema != 0 ? bySchema : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        logger.LogInformation(
            "Read catalog for [{Database}] on {Server}: {Objects} objects",
            databaseName, serverName, objects.Count);

        return new CatalogSnapshot
        {
            ServerName = serverName,
            DatabaseName = databaseName,
            Objects = objects,
            CapturedAt = DateTime.UtcNow
        };
    }

    private static async Task<(string ServerName, string DatabaseName)> ReadServerAndDatabaseAsync(
        DbConnection conn, CancellationToken ct)
    {
        // @@SERVERNAME is NULL on an instance that was renamed without sp_dropserver/sp_addserver;
        // SERVERPROPERTY always answers, but it returns sql_variant, which ISNULL cannot
        // implicitly convert against @@SERVERNAME's nvarchar -- convert it explicitly first.
        const string sql = """
            SELECT
                ISNULL(@@SERVERNAME, CONVERT(nvarchar(256), SERVERPROPERTY('ServerName'))) AS server_name,
                DB_NAME() AS database_name
            """;

        await using var cmd = CreateCommand(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (string.Empty, string.Empty);

        return (
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    private sealed record CatalogRow(int ObjectId, string Schema, string Name, string TypeCode, DateTime? ModifiedAt);

    /// <summary>
    /// One pass over <c>sys.objects</c> for everything the browser shows, plus one over
    /// <c>sys.table_types</c>: a table type's <c>sys.objects</c> row lives in an internal schema,
    /// so joining it to <c>sys.schemas</c> would report the wrong owner.
    /// </summary>
    private static async Task<List<CatalogRow>> ReadObjectRowsAsync(DbConnection conn, CancellationToken ct)
    {
        // 'TT' is deliberately absent from the sys.objects list: table types come from
        // sys.table_types, whose sys.objects row sits in an internal schema.
        const string sql = """
            SELECT o.object_id, s.name AS schema_name, o.name, o.type, o.modify_date
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U','V','P','PC','FN','FS','IF','TF','FT','SN','SO','TR')

            UNION ALL

            SELECT tt.type_table_object_id, s.name AS schema_name, tt.name, 'TT', o.modify_date
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            LEFT JOIN sys.objects o ON o.object_id = tt.type_table_object_id
            WHERE tt.is_user_defined = 1
            """;

        await using var cmd = CreateCommand(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<CatalogRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CatalogRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).Trim(),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }

        return rows;
    }

    private static async Task<Dictionary<int, long>> ReadRowCountsAsync(DbConnection conn, CancellationToken ct)
    {
        // index_id 0 is the heap, 1 the clustered index; a table has exactly one of them, so
        // summing across partitions of both gives the row count without double counting.
        const string sql = """
            SELECT ps.object_id, CAST(SUM(ps.row_count) AS bigint) AS row_count
            FROM sys.dm_db_partition_stats ps
            WHERE ps.index_id IN (0, 1)
            GROUP BY ps.object_id
            """;

        await using var cmd = CreateCommand(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var counts = new Dictionary<int, long>();
        while (await reader.ReadAsync(ct))
            counts[reader.GetInt32(0)] = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);

        return counts;
    }

    private static async Task<Dictionary<int, List<CatalogParameter>>> ReadParametersAsync(
        DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                p.object_id,
                p.name,
                tp.name AS type_name,
                ts.name AS type_schema,
                tp.is_user_defined,
                p.max_length,
                p.precision,
                p.scale,
                p.is_output,
                p.has_default_value
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.types tp ON tp.user_type_id = p.user_type_id
            JOIN sys.schemas ts ON ts.schema_id = tp.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('P','PC','FN','FS','IF','TF','FT')
              AND p.parameter_id > 0
            ORDER BY p.object_id, p.parameter_id
            """;

        await using var cmd = CreateCommand(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byObject = new Dictionary<int, List<CatalogParameter>>();
        while (await reader.ReadAsync(ct))
        {
            var objectId = reader.GetInt32(0);
            var parameter = new CatalogParameter
            {
                Name = reader.GetString(1),
                DataType = RenderDataType(
                    reader.GetString(2),
                    reader.GetString(3),
                    ReadBooleanLoose(reader, 4),
                    ReadInt32Loose(reader, 5),
                    ReadInt32Loose(reader, 6),
                    ReadInt32Loose(reader, 7)),
                IsOutput = ReadBooleanLoose(reader, 8),
                HasDefault = ReadBooleanLoose(reader, 9)
            };

            if (!byObject.TryGetValue(objectId, out var list))
                byObject[objectId] = list = [];
            list.Add(parameter);
        }

        return byObject;
    }

    private static CatalogObjectCategory? MapCategory(string typeCode) => typeCode switch
    {
        "U" => CatalogObjectCategory.Table,
        "V" => CatalogObjectCategory.View,
        "P" or "PC" => CatalogObjectCategory.StoredProcedure,
        "FN" or "FS" or "IF" or "TF" or "FT" => CatalogObjectCategory.Function,
        "SN" => CatalogObjectCategory.Synonym,
        "SO" => CatalogObjectCategory.Sequence,
        "TT" => CatalogObjectCategory.TableType,
        "TR" => CatalogObjectCategory.Trigger,
        _ => null
    };

    #endregion

    #region Definitions and columns

    public async Task<string?> GetObjectDefinitionAsync(
        string connectionString, string schema, string name, CancellationToken ct = default)
    {
        const string sql = """
            SELECT m.definition
            FROM sys.sql_modules m
            WHERE m.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))
            """;

        await using var conn = await OpenAsync(connectionString, ct);
        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        // definition is NULL for an encrypted module, and the row is absent for a table.
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string connectionString, string schema, string name, CancellationToken ct = default)
    {
        // Shape matches SchemaDiscoveryService: MaxLength is the raw catalog value in bytes,
        // not the character count, so both producers of ColumnInfo mean the same thing.
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
            LEFT JOIN sys.default_constraints dc
                ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))
            ORDER BY c.column_id
            """;

        await using var conn = await OpenAsync(connectionString, ct);
        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                MaxLength = reader.IsDBNull(2) ? null : ReadInt32Loose(reader, 2),
                Precision = reader.IsDBNull(3) ? null : ReadInt32Loose(reader, 3),
                Scale = reader.IsDBNull(4) ? null : ReadInt32Loose(reader, 4),
                IsNullable = ReadBooleanLoose(reader, 5),
                IsIdentity = ReadBooleanLoose(reader, 6),
                IsComputed = ReadBooleanLoose(reader, 7),
                DefaultValue = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return columns;
    }

    #endregion

    #region CREATE TABLE reconstruction

    public async Task<string> ScriptCreateTableAsync(
        string connectionString, string schema, string table, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(connectionString, ct);

        var columns = await ReadTableColumnsAsync(conn, schema, table, ct);
        if (columns.Count == 0)
        {
            logger.LogWarning("CREATE TABLE requested for {Object}, which has no visible columns",
                SqlIdentifier.QuoteQualified(schema, table));
            return $"-- {SqlIdentifier.QuoteQualified(schema, table)} was not found, or the current login cannot see its columns.";
        }

        var keys = await ReadKeyConstraintsAsync(conn, schema, table, ct);
        var indexes = await ReadIndexesAsync(conn, schema, table, ct);
        var foreignKeys = await ReadForeignKeysAsync(conn, schema, table, ct);

        return BuildCreateTableScript(schema, table, columns, keys, indexes, foreignKeys);
    }

    private sealed record TableColumn(
        string Name,
        string TypeName,
        string TypeSchema,
        bool IsUserDefinedType,
        int MaxLength,
        int Precision,
        int Scale,
        bool IsNullable,
        bool IsIdentity,
        string? IdentitySeed,
        string? IdentityIncrement,
        bool IsComputed,
        string? ComputedDefinition,
        bool IsPersisted,
        string? DefaultName,
        string? DefaultDefinition,
        string? CollationName,
        string? DatabaseCollation);

    private sealed record IndexColumn(string Column, bool Descending);

    private sealed record KeyConstraint(string Name, string Type, string IndexTypeDesc, List<IndexColumn> Columns);

    private sealed record TableIndex(
        int IndexId,
        string Name,
        int Type,
        string TypeDesc,
        bool IsUnique,
        string? FilterDefinition,
        List<IndexColumn> KeyColumns,
        List<string> IncludedColumns);

    private sealed record TableForeignKey(
        string Name,
        string ReferencedSchema,
        string ReferencedTable,
        List<string> Columns,
        List<string> ReferencedColumns,
        string DeleteAction,
        string UpdateAction,
        bool IsNotTrusted,
        bool IsDisabled);

    private static async Task<List<TableColumn>> ReadTableColumnsAsync(
        DbConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.name,
                tp.name AS type_name,
                ts.name AS type_schema,
                tp.is_user_defined,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                c.collation_name,
                CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)) AS database_collation,
                ic.seed_value,
                ic.increment_value,
                cc.definition AS computed_definition,
                cc.is_persisted,
                dc.name AS default_name,
                dc.definition AS default_definition
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id = tp.schema_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc
                ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY c.column_id
            """;

        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<TableColumn>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new TableColumn(
                Name: reader.GetString(0),
                TypeName: reader.GetString(1),
                TypeSchema: reader.GetString(2),
                IsUserDefinedType: ReadBooleanLoose(reader, 3),
                MaxLength: ReadInt32Loose(reader, 4),
                Precision: ReadInt32Loose(reader, 5),
                Scale: ReadInt32Loose(reader, 6),
                IsNullable: ReadBooleanLoose(reader, 7),
                IsIdentity: ReadBooleanLoose(reader, 8),
                IsComputed: ReadBooleanLoose(reader, 9),
                CollationName: reader.IsDBNull(10) ? null : reader.GetString(10),
                DatabaseCollation: reader.IsDBNull(11) ? null : reader.GetString(11),
                IdentitySeed: ReadVariantAsSqlLiteral(reader, 12),
                IdentityIncrement: ReadVariantAsSqlLiteral(reader, 13),
                ComputedDefinition: reader.IsDBNull(14) ? null : reader.GetString(14),
                IsPersisted: ReadBooleanLoose(reader, 15),
                DefaultName: reader.IsDBNull(16) ? null : reader.GetString(16),
                DefaultDefinition: reader.IsDBNull(17) ? null : reader.GetString(17)));
        }

        return columns;
    }

    private static async Task<List<KeyConstraint>> ReadKeyConstraintsAsync(
        DbConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT
                kc.name AS constraint_name,
                kc.type AS constraint_type,
                i.type_desc AS index_type_desc,
                c.name AS column_name,
                ic.is_descending_key
            FROM sys.key_constraints kc
            JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
              AND kc.type IN ('PK', 'UQ')
            ORDER BY kc.type, kc.name, ic.key_ordinal
            """;

        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byName = new Dictionary<string, KeyConstraint>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<KeyConstraint>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var constraint))
            {
                constraint = new KeyConstraint(name, reader.GetString(1).Trim(), reader.GetString(2), []);
                byName[name] = constraint;
                ordered.Add(constraint);
            }

            constraint.Columns.Add(new IndexColumn(reader.GetString(3), ReadBooleanLoose(reader, 4)));
        }

        return ordered;
    }

    private static async Task<List<TableIndex>> ReadIndexesAsync(
        DbConnection conn, string schema, string table, CancellationToken ct)
    {
        // Constraint-backed indexes are scripted inline with the table; a clustered columnstore
        // index has no key columns, hence the LEFT JOIN.
        const string sql = """
            SELECT
                i.index_id,
                i.name,
                i.type,
                i.type_desc,
                i.is_unique,
                i.filter_definition,
                c.name AS column_name,
                ic.is_included_column,
                ic.is_descending_key
            FROM sys.indexes i
            LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
              AND i.name IS NOT NULL
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.is_hypothetical = 0
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """;

        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byId = new Dictionary<int, TableIndex>();
        var ordered = new List<TableIndex>();
        while (await reader.ReadAsync(ct))
        {
            var indexId = reader.GetInt32(0);
            if (!byId.TryGetValue(indexId, out var index))
            {
                index = new TableIndex(
                    indexId,
                    reader.GetString(1),
                    ReadInt32Loose(reader, 2),
                    reader.GetString(3),
                    ReadBooleanLoose(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    [],
                    []);
                byId[indexId] = index;
                ordered.Add(index);
            }

            if (reader.IsDBNull(6))
                continue;

            var columnName = reader.GetString(6);
            if (ReadBooleanLoose(reader, 7))
                index.IncludedColumns.Add(columnName);
            else
                index.KeyColumns.Add(new IndexColumn(columnName, ReadBooleanLoose(reader, 8)));
        }

        return ordered;
    }

    private static async Task<List<TableForeignKey>> ReadForeignKeysAsync(
        DbConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT
                fk.name AS constraint_name,
                rs.name AS referenced_schema,
                rt.name AS referenced_table,
                pc.name AS parent_column,
                rc.name AS referenced_column,
                fk.delete_referential_action_desc,
                fk.update_referential_action_desc,
                fk.is_not_trusted,
                fk.is_disabled
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        await using var cmd = CreateCommand(conn, sql);
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byName = new Dictionary<string, TableForeignKey>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TableForeignKey>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var fk))
            {
                fk = new TableForeignKey(
                    name,
                    reader.GetString(1),
                    reader.GetString(2),
                    [],
                    [],
                    reader.IsDBNull(5) ? "NO_ACTION" : reader.GetString(5),
                    reader.IsDBNull(6) ? "NO_ACTION" : reader.GetString(6),
                    ReadBooleanLoose(reader, 7),
                    ReadBooleanLoose(reader, 8));
                byName[name] = fk;
                ordered.Add(fk);
            }

            fk.Columns.Add(reader.GetString(3));
            fk.ReferencedColumns.Add(reader.GetString(4));
        }

        return ordered;
    }

    private static string BuildCreateTableScript(
        string schema,
        string table,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<KeyConstraint> keys,
        IReadOnlyList<TableIndex> indexes,
        IReadOnlyList<TableForeignKey> foreignKeys)
    {
        var qualified = SqlIdentifier.QuoteQualified(schema, table);
        var notes = new List<string>();

        var members = new List<string>(columns.Count + keys.Count);
        foreach (var column in columns)
            members.Add(BuildColumnDefinition(column, notes));
        foreach (var key in keys)
            members.Add(BuildKeyConstraintDefinition(key));

        var body = new StringBuilder();
        body.Append("CREATE TABLE ").Append(qualified).Append(" (").Append(Nl);
        body.Append(string.Join("," + Nl, members.Select(m => "    " + m)));
        body.Append(Nl).Append(");").Append(Nl);

        var indexScripts = new List<string>();
        foreach (var index in indexes)
        {
            var script = BuildIndexScript(qualified, index, notes);
            if (script is not null)
                indexScripts.Add(script);
        }

        if (indexScripts.Count > 0)
        {
            body.Append(Nl);
            foreach (var script in indexScripts)
                body.Append(script).Append(Nl);
        }

        if (foreignKeys.Count > 0)
        {
            body.Append(Nl);
            foreach (var fk in foreignKeys)
            {
                body.Append(BuildForeignKeyScript(qualified, fk)).Append(Nl);
                if (fk.IsDisabled)
                    body.Append("ALTER TABLE ").Append(qualified)
                        .Append(" NOCHECK CONSTRAINT ").Append(SqlIdentifier.Quote(fk.Name)).Append(';').Append(Nl);
            }
        }

        if (notes.Count == 0)
            return body.ToString();

        var header = new StringBuilder();
        foreach (var note in notes)
        {
            // Identifiers may legally contain line breaks; folding them keeps the note on one
            // comment line instead of commenting out only its first line.
            header.Append("-- ").Append(note.Replace('\r', ' ').Replace('\n', ' ')).Append(Nl);
        }

        header.Append(Nl);
        return header.Append(body).ToString();
    }

    private static string BuildColumnDefinition(TableColumn column, List<string> notes)
    {
        var quotedName = SqlIdentifier.Quote(column.Name);
        var sb = new StringBuilder(quotedName).Append(' ');

        if (column.IsComputed)
        {
            if (string.IsNullOrWhiteSpace(column.ComputedDefinition))
            {
                notes.Add($"Computed column {quotedName} has no readable definition (encrypted, or the login lacks VIEW DEFINITION); a placeholder expression was written instead.");
                return sb.Append("AS (NULL)").ToString();
            }

            sb.Append("AS ").Append(Parenthesize(column.ComputedDefinition));
            if (column.IsPersisted)
            {
                sb.Append(" PERSISTED");
                // Only a persisted computed column may carry a nullability declaration.
                if (!column.IsNullable)
                    sb.Append(" NOT NULL");
            }

            return sb.ToString();
        }

        sb.Append(RenderDataType(
            column.TypeName, column.TypeSchema, column.IsUserDefinedType,
            column.MaxLength, column.Precision, column.Scale));

        if (column.CollationName is { Length: > 0 } collation &&
            !string.Equals(collation, column.DatabaseCollation, StringComparison.OrdinalIgnoreCase))
        {
            // COLLATE takes a bare collation name that cannot be bracket-quoted, so anything
            // that is not a plain identifier is reported rather than written into the script.
            if (CollationNamePattern().IsMatch(collation))
                sb.Append(" COLLATE ").Append(collation);
            else
                notes.Add($"Column {quotedName} uses collation '{collation}', which is not a plain identifier and was left out of the script.");
        }

        if (column.IsIdentity)
        {
            if (column.IdentitySeed is { Length: > 0 } seed && column.IdentityIncrement is { Length: > 0 } increment)
                sb.Append(" IDENTITY(").Append(seed).Append(',').Append(increment).Append(')');
            else
            {
                notes.Add($"Identity seed and increment for {quotedName} were not readable; IDENTITY(1,1) was assumed.");
                sb.Append(" IDENTITY(1,1)");
            }
        }

        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (column.DefaultDefinition is { Length: > 0 } defaultDefinition)
        {
            sb.Append(' ');
            if (column.DefaultName is { Length: > 0 } defaultName)
                sb.Append("CONSTRAINT ").Append(SqlIdentifier.Quote(defaultName)).Append(' ');
            sb.Append("DEFAULT ").Append(Parenthesize(defaultDefinition));
        }

        return sb.ToString();
    }

    private static string BuildKeyConstraintDefinition(KeyConstraint key)
    {
        var kind = key.Type == "PK" ? "PRIMARY KEY" : "UNIQUE";
        var clustering = key.IndexTypeDesc.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase)
            ? "CLUSTERED"
            : "NONCLUSTERED";

        return $"CONSTRAINT {SqlIdentifier.Quote(key.Name)} {kind} {clustering} ({FormatKeyColumns(key.Columns)})";
    }

    private static string? BuildIndexScript(string qualified, TableIndex index, List<string> notes)
    {
        var quotedName = SqlIdentifier.Quote(index.Name);

        switch (index.Type)
        {
            case 1:
            case 2:
            {
                if (index.KeyColumns.Count == 0)
                {
                    notes.Add($"Index {quotedName} reports no key columns and was not scripted.");
                    return null;
                }

                var sb = new StringBuilder("CREATE ");
                if (index.IsUnique) sb.Append("UNIQUE ");
                sb.Append(index.Type == 1 ? "CLUSTERED" : "NONCLUSTERED").Append(" INDEX ")
                    .Append(quotedName).Append(" ON ").Append(qualified)
                    .Append(" (").Append(FormatKeyColumns(index.KeyColumns)).Append(')');

                if (index.IncludedColumns.Count > 0)
                    sb.Append(" INCLUDE (")
                        .Append(string.Join(", ", index.IncludedColumns.Select(SqlIdentifier.Quote)))
                        .Append(')');

                if (!string.IsNullOrWhiteSpace(index.FilterDefinition))
                    sb.Append(" WHERE ").Append(index.FilterDefinition);

                return sb.Append(';').ToString();
            }

            case 5:
                return $"CREATE CLUSTERED COLUMNSTORE INDEX {quotedName} ON {qualified};";

            case 6:
            {
                var columns = index.KeyColumns.Select(c => SqlIdentifier.Quote(c.Column))
                    .Concat(index.IncludedColumns.Select(SqlIdentifier.Quote))
                    .ToList();

                if (columns.Count == 0)
                {
                    notes.Add($"Nonclustered columnstore index {quotedName} reports no columns and was not scripted.");
                    return null;
                }

                return $"CREATE NONCLUSTERED COLUMNSTORE INDEX {quotedName} ON {qualified} ({string.Join(", ", columns)});";
            }

            default:
                notes.Add($"Index {quotedName} ({index.TypeDesc}) was not scripted — only rowstore and columnstore indexes are reconstructed.");
                return null;
        }
    }

    private static string BuildForeignKeyScript(string qualified, TableForeignKey fk)
    {
        var sb = new StringBuilder("ALTER TABLE ").Append(qualified)
            .Append(fk.IsNotTrusted ? " WITH NOCHECK" : " WITH CHECK")
            .Append(" ADD CONSTRAINT ").Append(SqlIdentifier.Quote(fk.Name))
            .Append(" FOREIGN KEY (")
            .Append(string.Join(", ", fk.Columns.Select(SqlIdentifier.Quote)))
            .Append(") REFERENCES ")
            .Append(SqlIdentifier.QuoteQualified(fk.ReferencedSchema, fk.ReferencedTable))
            .Append(" (")
            .Append(string.Join(", ", fk.ReferencedColumns.Select(SqlIdentifier.Quote)))
            .Append(')');

        if (!fk.DeleteAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON DELETE ").Append(fk.DeleteAction.Replace('_', ' '));
        if (!fk.UpdateAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON UPDATE ").Append(fk.UpdateAction.Replace('_', ' '));

        return sb.Append(';').ToString();
    }

    private static string FormatKeyColumns(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c => $"{SqlIdentifier.Quote(c.Column)} {(c.Descending ? "DESC" : "ASC")}"));

    /// <summary>Catalog expressions arrive already wrapped in parentheses; older builds occasionally do not.</summary>
    private static string Parenthesize(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? trimmed : $"({trimmed})";
    }

    #endregion

    #region Script building

    public async Task<string> BuildScriptAsync(
        string connectionString,
        CatalogObject catalogObject,
        CatalogScriptAction action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);

        return action switch
        {
            CatalogScriptAction.SelectTop1000 => BuildSelectTop(catalogObject),
            CatalogScriptAction.SelectCount => BuildSelectCount(catalogObject),
            CatalogScriptAction.ExecuteTemplate => BuildExecuteTemplate(catalogObject),
            CatalogScriptAction.ScriptDefinition =>
                await BuildDefinitionScriptAsync(connectionString, catalogObject, alter: false, ct),
            CatalogScriptAction.ScriptAlter =>
                await BuildDefinitionScriptAsync(connectionString, catalogObject, alter: true, ct),
            CatalogScriptAction.ScriptCreateTable =>
                await ScriptCreateTableAsync(connectionString, catalogObject.Schema, catalogObject.Name, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown catalog script action.")
        };
    }

    private static string BuildSelectTop(CatalogObject o) => o.Category switch
    {
        CatalogObjectCategory.Table or CatalogObjectCategory.View or CatalogObjectCategory.Synonym =>
            $"SELECT TOP (1000) *{Nl}FROM {o.QuotedFullName};",
        CatalogObjectCategory.Function when IsTableValued(o.TypeCode) =>
            BuildFunctionInvocation(o, top: true),
        _ => $"-- SELECT is not applicable to {o.QuotedFullName} ({o.TypeCode})."
    };

    private static string BuildSelectCount(CatalogObject o)
    {
        switch (o.Category)
        {
            case CatalogObjectCategory.Table:
            case CatalogObjectCategory.View:
            case CatalogObjectCategory.Synonym:
                return $"SELECT COUNT_BIG(*) AS [Rows] FROM {o.QuotedFullName};";

            case CatalogObjectCategory.Function when IsTableValued(o.TypeCode):
            {
                var (declarations, args) = BuildParameterDeclarations(o);
                return $"{declarations}SELECT COUNT_BIG(*) AS [Rows] FROM {o.QuotedFullName}({args});";
            }

            default:
                return $"-- SELECT COUNT is not applicable to {o.QuotedFullName} ({o.TypeCode}).";
        }
    }

    private static string BuildExecuteTemplate(CatalogObject o) => o.Category switch
    {
        CatalogObjectCategory.StoredProcedure => BuildProcedureExec(o),
        CatalogObjectCategory.Function => BuildFunctionInvocation(o, top: false),
        _ => $"-- {o.QuotedFullName} ({o.TypeCode}) is not executable."
    };

    private static string BuildProcedureExec(CatalogObject o)
    {
        if (o.Parameters.Count == 0)
            return $"EXEC {o.QuotedFullName};";

        var (declarations, _) = BuildParameterDeclarations(o);

        var arguments = o.Parameters.Select(p =>
            $"    {p.Name} = {p.Name}{(p.IsOutput ? " OUTPUT" : string.Empty)}");

        return $"{declarations}EXEC {o.QuotedFullName}{Nl}{string.Join("," + Nl, arguments)};";
    }

    private static string BuildFunctionInvocation(CatalogObject o, bool top)
    {
        var (declarations, args) = BuildParameterDeclarations(o);

        if (IsTableValued(o.TypeCode))
        {
            var select = top ? "SELECT TOP (1000) *" : "SELECT *";
            return $"{declarations}{select}{Nl}FROM {o.QuotedFullName}({args});";
        }

        return $"{declarations}SELECT {o.QuotedFullName}({args}) AS [Result];";
    }

    /// <summary>
    /// Renders a <c>DECLARE</c> for every parameter — output parameters included, since a value
    /// still has to be passed in for the server to write back into — plus the comma-separated
    /// argument list for a function call. Returns empty strings when there are no parameters.
    /// </summary>
    private static (string Declarations, string Args) BuildParameterDeclarations(CatalogObject o)
    {
        if (o.Parameters.Count == 0)
            return (string.Empty, string.Empty);

        var sb = new StringBuilder();
        foreach (var p in o.Parameters)
        {
            sb.Append("DECLARE ").Append(p.Name).Append(' ').Append(p.DataType).Append(';')
                .Append(p.IsOutput ? " -- receives the output value" : " -- set me")
                .Append(p.HasDefault ? " (optional)" : string.Empty)
                .Append(Nl);
        }

        sb.Append(Nl);
        return (sb.ToString(), string.Join(", ", o.Parameters.Select(p => p.Name)));
    }

    private async Task<string> BuildDefinitionScriptAsync(
        string connectionString, CatalogObject o, bool alter, CancellationToken ct)
    {
        var definition = await GetObjectDefinitionAsync(connectionString, o.Schema, o.Name, ct);
        if (definition is null)
            return $"-- No stored definition is available for {o.QuotedFullName}. It is either not a module, encrypted, or CLR-based.";

        if (!alter)
            return definition;

        var rewritten = RewriteCreateToAlter(definition);
        if (rewritten is not null)
            return rewritten;

        logger.LogDebug("Could not rewrite CREATE to ALTER for {Object}", o.QuotedFullName);
        return $"-- The leading CREATE keyword could not be located; the definition below is unchanged.{Nl}{definition}";
    }

    /// <summary>
    /// Replaces the module's leading <c>CREATE</c> (or <c>CREATE OR ALTER</c>) with <c>ALTER</c>.
    /// Leading whitespace and comments are skipped first so a <c>CREATE</c> mentioned in a header
    /// comment is never mistaken for the real one. Returns null when the header does not match.
    /// </summary>
    internal static string? RewriteCreateToAlter(string definition)
    {
        var start = SkipLeadingTrivia(definition);
        if (start >= definition.Length)
            return null;

        // \G plus the explicit index check keeps the match pinned to the first real token, so a
        // CREATE further down the body can never be rewritten.
        var match = CreateHeaderPattern().Match(definition, start);
        if (!match.Success || match.Index != start)
            return null;

        return definition[..match.Index]
            + "ALTER "
            + match.Groups["kind"].Value
            + definition[(match.Index + match.Length)..];
    }

    /// <summary>Index of the first character that is neither whitespace nor part of a leading comment.</summary>
    private static int SkipLeadingTrivia(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
                continue;
            }

            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }

            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            break;
        }

        return i;
    }

    private static bool IsTableValued(string typeCode) => typeCode is "IF" or "TF" or "FT";

    #endregion

    #region Type rendering

    /// <summary>
    /// Renders a catalog type the way a <c>DECLARE</c> or a column definition needs it:
    /// <c>nvarchar(50)</c>, <c>nvarchar(max)</c>, <c>decimal(18,2)</c>, <c>datetime2(7)</c>, or a
    /// bare name for types without facets. Alias and table types render schema-qualified, since
    /// facets are fixed by the type itself.
    /// </summary>
    private static string RenderDataType(
        string typeName, string typeSchema, bool isUserDefined, int maxLength, int precision, int scale)
    {
        if (isUserDefined)
            return SqlIdentifier.QuoteQualified(typeSchema, typeName);

        return typeName.ToLowerInvariant() switch
        {
            // max_length is in bytes for the national character types.
            "nvarchar" or "nchar" => $"{typeName}({LengthFacet(maxLength, halve: true)})",
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({LengthFacet(maxLength, halve: false)})",
            "decimal" or "numeric" => $"{typeName}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale.ToString(CultureInfo.InvariantCulture)})",
            _ => typeName
        };
    }

    private static string LengthFacet(int maxLength, bool halve) =>
        maxLength == -1
            ? "max"
            : (halve ? maxLength / 2 : maxLength).ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Helpers

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static DbCommand CreateCommand(DbConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        return cmd;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Reads a catalog integer that may surface as <see cref="byte"/> (<c>tinyint</c>),
    /// <see cref="short"/> (<c>smallint</c>), or <see cref="int"/> depending on the column.
    /// </summary>
    private static int ReadInt32Loose(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        return reader.GetValue(ordinal) switch
        {
            int i => i,
            byte b => b,
            short s => s,
            long l => checked((int)l),
            bool b => b ? 1 : 0,
            var v => Convert.ToInt32(v, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Reads a catalog flag, treating a NULL from an outer join as false.</summary>
    private static bool ReadBooleanLoose(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;

        return reader.GetValue(ordinal) switch
        {
            bool b => b,
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            var v => Convert.ToBoolean(v, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Renders a <c>sql_variant</c> identity seed or increment as a SQL numeric literal.
    /// Returns null when the value is absent, so the caller can report the gap instead of guessing.
    /// </summary>
    private static string? ReadVariantAsSqlLiteral(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetValue(ordinal) switch
        {
            byte b => b.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    [GeneratedRegex(
        @"\GCREATE\s+(?:OR\s+ALTER\s+)?(?<kind>PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateHeaderPattern();

    // \z rather than $ so a trailing newline cannot smuggle anything past the COLLATE guard.
    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z", RegexOptions.Compiled)]
    private static partial Regex CollationNamePattern();

    #endregion
}
