namespace Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

/// <summary>
/// Top-level grouping the object browser shows as folders. Narrower than
/// <c>SqlObjectKind</c>: several catalog types collapse into one folder
/// (all three function types land in <see cref="Function"/>).
/// </summary>
public enum CatalogObjectCategory
{
    Table,
    View,
    StoredProcedure,
    Function,
    Synonym,
    Sequence,
    TableType,
    Trigger
}

/// <summary>One object in the browser tree.</summary>
public sealed record CatalogObject
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required CatalogObjectCategory Category { get; init; }

    /// <summary>Raw <c>sys.objects.type</c> code (<c>U</c>, <c>V</c>, <c>P</c>, <c>FN</c>, <c>IF</c>, <c>TF</c>, …).</summary>
    public required string TypeCode { get; init; }

    public int ObjectId { get; init; }

    /// <summary>Rows for tables (from <c>sys.dm_db_partition_stats</c>), null for everything else.</summary>
    public long? RowCount { get; init; }

    public DateTime? ModifiedAt { get; init; }

    /// <summary>Parameters for procedures and functions, in ordinal order. Empty otherwise.</summary>
    public IReadOnlyList<CatalogParameter> Parameters { get; init; } = [];

    /// <summary><c>[schema].[name]</c>, always bracket-quoted.</summary>
    public string QuotedFullName => $"[{Schema.Replace("]", "]]")}].[{Name.Replace("]", "]]")}]";
}

/// <summary>A procedure or function parameter, used to build EXEC templates.</summary>
public sealed record CatalogParameter
{
    /// <summary>Parameter name including the leading <c>@</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Type rendered for a DECLARE, e.g. <c>nvarchar(50)</c> or <c>decimal(18,2)</c>.</summary>
    public required string DataType { get; init; }

    public bool IsOutput { get; init; }
    public bool HasDefault { get; init; }
}

/// <summary>A schema and the objects under it, for one category.</summary>
public sealed record CatalogSchemaGroup
{
    public required string Schema { get; init; }
    public required IReadOnlyList<CatalogObject> Objects { get; init; }
}

/// <summary>Everything the browser tree needs for one database connection.</summary>
public sealed record CatalogSnapshot
{
    public required string DatabaseName { get; init; }
    public required string ServerName { get; init; }
    public required IReadOnlyList<CatalogObject> Objects { get; init; }

    /// <summary>When the catalog was read (UTC), so the UI can show staleness.</summary>
    public DateTime CapturedAt { get; init; }

    public IEnumerable<CatalogSchemaGroup> GroupBySchema(CatalogObjectCategory category) =>
        Objects
            .Where(o => o.Category == category)
            .GroupBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CatalogSchemaGroup
            {
                Schema = g.Key,
                Objects = g.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList()
            });
}

/// <summary>The scripting actions the object browser's context menu offers.</summary>
public enum CatalogScriptAction
{
    /// <summary><c>SELECT TOP (1000) * FROM [schema].[obj];</c></summary>
    SelectTop1000,

    /// <summary><c>SELECT COUNT_BIG(*) FROM [schema].[obj];</c></summary>
    SelectCount,

    /// <summary>DECLARE block for every parameter followed by <c>EXEC [schema].[proc] @p = @p …</c>.</summary>
    ExecuteTemplate,

    /// <summary>The module's stored definition from <c>sys.sql_modules</c>.</summary>
    ScriptDefinition,

    /// <summary>The module's definition rewritten from CREATE to ALTER.</summary>
    ScriptAlter,

    /// <summary>A <c>CREATE TABLE</c> reconstructed from catalog metadata, with indexes and constraints.</summary>
    ScriptCreateTable
}
