namespace Tedd.AIOptimizeSql.OptimizeEngine.Models;

/// <summary>
/// Structured output of deterministic schema discovery.
/// Contains the full object graph, dependency edges, base table metadata,
/// and warnings for edge cases the optimizer should be aware of.
/// </summary>
public sealed class SchemaDiscoveryResult
{
    public required IReadOnlyList<DiscoveredObject> Objects { get; init; }
    public required IReadOnlyList<DependencyEdge> Dependencies { get; init; }
    public required IReadOnlyList<BaseTableInfo> BaseTables { get; init; }
    public required IReadOnlyList<DiscoveryWarning> Warnings { get; init; }
    public required string MarkdownSummary { get; init; }
}

public enum SqlObjectKind
{
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    InlineTableValuedFunction,
    Trigger,
    Synonym,
    Sequence,
    TableType,
    Unknown
}

public sealed class DiscoveredObject
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required SqlObjectKind Kind { get; init; }
    public int ObjectId { get; init; }
    public string? Definition { get; init; }
    public IReadOnlyList<ObjectParameterInfo>? Parameters { get; init; }
    public IReadOnlyList<ColumnInfo>? Columns { get; init; }

    public bool IsEncrypted { get; init; }
    public bool IsSchemaBound { get; init; }
    public bool IsMemoryOptimized { get; init; }
    public bool IsTemporal { get; init; }
    public bool IsExternal { get; init; }
    public bool IsClr { get; init; }
    public bool HasDynamicSqlLikely { get; init; }
}

public sealed class DependencyEdge
{
    public required string ReferencingSchema { get; init; }
    public required string ReferencingName { get; init; }
    public required string ReferencedSchema { get; init; }
    public required string ReferencedName { get; init; }
    public string? ReferencedColumnName { get; init; }
    public bool IsSchemabound { get; init; }
}

public sealed class BaseTableInfo
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public IReadOnlyList<ColumnInfo>? Columns { get; init; }
    public IReadOnlyList<IndexInfo>? Indexes { get; init; }
    public IReadOnlyList<ForeignKeyInfo>? ForeignKeys { get; init; }
    public string? CompressionState { get; init; }
    public string? PartitionInfo { get; init; }
    public long? RowCount { get; init; }
    public long? PageCount { get; init; }
    public bool IsMemoryOptimized { get; init; }
    public bool IsTemporal { get; init; }
    public bool HasColumnstore { get; init; }
}

public sealed class ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsComputed { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed class IndexInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public required IReadOnlyList<string> KeyColumns { get; init; }
    public IReadOnlyList<string>? IncludedColumns { get; init; }
    public string? FilterDefinition { get; init; }
    public string? CompressionType { get; init; }
}

public sealed class ForeignKeyInfo
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required string ReferencedSchema { get; init; }
    public required string ReferencedTable { get; init; }
    public required IReadOnlyList<string> ReferencedColumns { get; init; }
}

public sealed class ObjectParameterInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsOutput { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed class DiscoveryWarning
{
    public required string ObjectName { get; init; }
    public required string Message { get; init; }
}
