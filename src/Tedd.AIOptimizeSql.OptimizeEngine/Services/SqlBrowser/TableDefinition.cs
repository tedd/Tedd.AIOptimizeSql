namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// The physical design of one table as the catalog describes it. Deliberately a plain data
/// model with no rendering in it: the object browser scripts it back at its own name, while the
/// sandbox generator scripts the same definition at a different schema or database.
/// </summary>
internal sealed record TableDefinition
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<TableColumnDefinition> Columns { get; init; }
    public required IReadOnlyList<TableKeyDefinition> Keys { get; init; }
    public required IReadOnlyList<TableIndexDefinition> Indexes { get; init; }
    public required IReadOnlyList<TableForeignKeyDefinition> ForeignKeys { get; init; }

    /// <summary>True when a row's values cannot simply be inserted: the column is generated.</summary>
    public static bool IsGenerated(TableColumnDefinition column) =>
        column.IsComputed ||
        column.TypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
        column.TypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase);

    /// <summary>Columns an <c>INSERT ... SELECT</c> copy can carry across, in table order.</summary>
    public IReadOnlyList<TableColumnDefinition> CopyableColumns =>
        Columns.Where(c => !IsGenerated(c)).ToList();

    public bool HasIdentity => Columns.Any(c => c.IsIdentity);
}

internal sealed record TableColumnDefinition(
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

internal sealed record TableIndexColumn(string Column, bool Descending);

/// <summary><c>Type</c> is the catalog's <c>PK</c> or <c>UQ</c>.</summary>
internal sealed record TableKeyDefinition(
    string Name, string Type, string IndexTypeDesc, List<TableIndexColumn> Columns);

internal sealed record TableIndexDefinition(
    int IndexId,
    string Name,
    int Type,
    string TypeDesc,
    bool IsUnique,
    string? FilterDefinition,
    List<TableIndexColumn> KeyColumns,
    List<string> IncludedColumns);

internal sealed record TableForeignKeyDefinition(
    string Name,
    string ReferencedSchema,
    string ReferencedTable,
    List<string> Columns,
    List<string> ReferencedColumns,
    string DeleteAction,
    string UpdateAction,
    bool IsNotTrusted,
    bool IsDisabled);
