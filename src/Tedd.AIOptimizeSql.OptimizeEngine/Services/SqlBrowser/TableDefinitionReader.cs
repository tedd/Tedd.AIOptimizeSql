using System.Data.Common;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Reads a table's physical design from <c>sys.*</c>. Every statement is a <c>SELECT</c>, so it
/// is safe on connections flagged analyze-only.
/// </summary>
internal static class TableDefinitionReader
{
    /// <summary>
    /// Returns the definition of <paramref name="schema"/>.<paramref name="table"/>, or null when
    /// the table does not exist or the current login cannot see its columns.
    /// </summary>
    public static async Task<TableDefinition?> ReadAsync(
        DbConnection conn, string schema, string table, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(conn, schema, table, ct);
        if (columns.Count == 0)
            return null;

        return new TableDefinition
        {
            Schema = schema,
            Table = table,
            Columns = columns,
            Keys = await ReadKeyConstraintsAsync(conn, schema, table, ct),
            Indexes = await ReadIndexesAsync(conn, schema, table, ct),
            ForeignKeys = await ReadForeignKeysAsync(conn, schema, table, ct)
        };
    }

    private static async Task<List<TableColumnDefinition>> ReadColumnsAsync(
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

        await using var cmd = CatalogRead.CreateCommand(conn, sql);
        CatalogRead.AddParam(cmd, "@schema", schema);
        CatalogRead.AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<TableColumnDefinition>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new TableColumnDefinition(
                Name: reader.GetString(0),
                TypeName: reader.GetString(1),
                TypeSchema: reader.GetString(2),
                IsUserDefinedType: CatalogRead.BooleanLoose(reader, 3),
                MaxLength: CatalogRead.Int32Loose(reader, 4),
                Precision: CatalogRead.Int32Loose(reader, 5),
                Scale: CatalogRead.Int32Loose(reader, 6),
                IsNullable: CatalogRead.BooleanLoose(reader, 7),
                IsIdentity: CatalogRead.BooleanLoose(reader, 8),
                IsComputed: CatalogRead.BooleanLoose(reader, 9),
                CollationName: reader.IsDBNull(10) ? null : reader.GetString(10),
                DatabaseCollation: reader.IsDBNull(11) ? null : reader.GetString(11),
                IdentitySeed: CatalogRead.VariantAsSqlLiteral(reader, 12),
                IdentityIncrement: CatalogRead.VariantAsSqlLiteral(reader, 13),
                ComputedDefinition: reader.IsDBNull(14) ? null : reader.GetString(14),
                IsPersisted: CatalogRead.BooleanLoose(reader, 15),
                DefaultName: reader.IsDBNull(16) ? null : reader.GetString(16),
                DefaultDefinition: reader.IsDBNull(17) ? null : reader.GetString(17)));
        }

        return columns;
    }

    private static async Task<List<TableKeyDefinition>> ReadKeyConstraintsAsync(
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

        await using var cmd = CatalogRead.CreateCommand(conn, sql);
        CatalogRead.AddParam(cmd, "@schema", schema);
        CatalogRead.AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byName = new Dictionary<string, TableKeyDefinition>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TableKeyDefinition>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var constraint))
            {
                constraint = new TableKeyDefinition(name, reader.GetString(1).Trim(), reader.GetString(2), []);
                byName[name] = constraint;
                ordered.Add(constraint);
            }

            constraint.Columns.Add(new TableIndexColumn(reader.GetString(3), CatalogRead.BooleanLoose(reader, 4)));
        }

        return ordered;
    }

    private static async Task<List<TableIndexDefinition>> ReadIndexesAsync(
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

        await using var cmd = CatalogRead.CreateCommand(conn, sql);
        CatalogRead.AddParam(cmd, "@schema", schema);
        CatalogRead.AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byId = new Dictionary<int, TableIndexDefinition>();
        var ordered = new List<TableIndexDefinition>();
        while (await reader.ReadAsync(ct))
        {
            var indexId = reader.GetInt32(0);
            if (!byId.TryGetValue(indexId, out var index))
            {
                index = new TableIndexDefinition(
                    indexId,
                    reader.GetString(1),
                    CatalogRead.Int32Loose(reader, 2),
                    reader.GetString(3),
                    CatalogRead.BooleanLoose(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    [],
                    []);
                byId[indexId] = index;
                ordered.Add(index);
            }

            if (reader.IsDBNull(6))
                continue;

            var columnName = reader.GetString(6);
            if (CatalogRead.BooleanLoose(reader, 7))
                index.IncludedColumns.Add(columnName);
            else
                index.KeyColumns.Add(new TableIndexColumn(columnName, CatalogRead.BooleanLoose(reader, 8)));
        }

        return ordered;
    }

    private static async Task<List<TableForeignKeyDefinition>> ReadForeignKeysAsync(
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

        await using var cmd = CatalogRead.CreateCommand(conn, sql);
        CatalogRead.AddParam(cmd, "@schema", schema);
        CatalogRead.AddParam(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var byName = new Dictionary<string, TableForeignKeyDefinition>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TableForeignKeyDefinition>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var fk))
            {
                fk = new TableForeignKeyDefinition(
                    name,
                    reader.GetString(1),
                    reader.GetString(2),
                    [],
                    [],
                    reader.IsDBNull(5) ? "NO_ACTION" : reader.GetString(5),
                    reader.IsDBNull(6) ? "NO_ACTION" : reader.GetString(6),
                    CatalogRead.BooleanLoose(reader, 7),
                    CatalogRead.BooleanLoose(reader, 8));
                byName[name] = fk;
                ordered.Add(fk);
            }

            fk.Columns.Add(reader.GetString(3));
            fk.ReferencedColumns.Add(reader.GetString(4));
        }

        return ordered;
    }
}
