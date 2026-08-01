using System.Data.Common;
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
                DataType = TableScriptWriter.RenderDataType(
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

        var definition = await TableDefinitionReader.ReadAsync(conn, schema, table, ct);
        if (definition is null)
        {
            logger.LogWarning("CREATE TABLE requested for {Object}, which has no visible columns",
                SqlIdentifier.QuoteQualified(schema, table));
            return $"-- {SqlIdentifier.QuoteQualified(schema, table)} was not found, or the current login cannot see its columns.";
        }

        return TableScriptWriter.BuildCatalogScript(definition);
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

    #region Helpers

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static DbCommand CreateCommand(DbConnection conn, string sql) =>
        CatalogRead.CreateCommand(conn, sql);

    private static void AddParam(DbCommand cmd, string name, object value) =>
        CatalogRead.AddParam(cmd, name, value);

    private static int ReadInt32Loose(DbDataReader reader, int ordinal) =>
        CatalogRead.Int32Loose(reader, ordinal);

    private static bool ReadBooleanLoose(DbDataReader reader, int ordinal) =>
        CatalogRead.BooleanLoose(reader, ordinal);

    [GeneratedRegex(
        @"\GCREATE\s+(?:OR\s+ALTER\s+)?(?<kind>PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateHeaderPattern();

    #endregion
}
