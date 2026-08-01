using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Renders a <see cref="TableDefinition"/> back into T-SQL. The target schema and table are
/// always passed in rather than taken from the definition, so the same catalog read can be
/// scripted at its own name (the object browser) or at a sandbox copy's name (the sandbox
/// generator). Anything that could not be reproduced faithfully is appended to a notes list
/// instead of being silently dropped.
/// </summary>
internal static partial class TableScriptWriter
{
    /// <summary>Generated SQL uses <c>\n</c> so a script looks identical whoever produced it.</summary>
    public const string Nl = "\n";

    /// <summary>
    /// The object browser's <c>CREATE TABLE</c> script: the table at its own name, followed by
    /// its indexes and foreign keys, with any notes as a leading comment block.
    /// </summary>
    public static string BuildCatalogScript(TableDefinition def)
    {
        var notes = new List<string>();
        var qualified = SqlIdentifier.QuoteQualified(def.Schema, def.Table);

        var body = new StringBuilder();
        body.Append(RenderCreateTable(def, def.Schema, def.Table, notes)).Append(Nl);

        var indexScripts = new List<string>();
        foreach (var index in def.Indexes)
        {
            var script = RenderIndex(qualified, index, notes);
            if (script is not null)
                indexScripts.Add(script);
        }

        if (indexScripts.Count > 0)
        {
            body.Append(Nl);
            foreach (var script in indexScripts)
                body.Append(script).Append(Nl);
        }

        if (def.ForeignKeys.Count > 0)
        {
            body.Append(Nl);
            foreach (var fk in def.ForeignKeys)
            {
                body.Append(RenderForeignKey(qualified, fk, fk.ReferencedSchema, fk.ReferencedTable)).Append(Nl);
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

    /// <summary>
    /// The <c>CREATE TABLE</c> statement alone — columns plus primary key and unique constraints,
    /// which have to be inline so a clustered key really is clustered. Indexes and foreign keys
    /// are rendered separately because they belong after the data copy.
    /// </summary>
    public static string RenderCreateTable(
        TableDefinition def, string targetSchema, string targetTable, List<string> notes)
    {
        var members = new List<string>(def.Columns.Count + def.Keys.Count);
        foreach (var column in def.Columns)
            members.Add(RenderColumn(column, notes));
        foreach (var key in def.Keys)
            members.Add(RenderKeyConstraint(key));

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(SqlIdentifier.QuoteQualified(targetSchema, targetTable))
            .Append(" (").Append(Nl);
        sb.Append(string.Join("," + Nl, members.Select(m => "    " + m)));
        sb.Append(Nl).Append(");");
        return sb.ToString();
    }

    public static string RenderColumn(TableColumnDefinition column, List<string> notes)
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

    public static string RenderKeyConstraint(TableKeyDefinition key)
    {
        var kind = key.Type == "PK" ? "PRIMARY KEY" : "UNIQUE";
        var clustering = key.IndexTypeDesc.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase)
            ? "CLUSTERED"
            : "NONCLUSTERED";

        return $"CONSTRAINT {SqlIdentifier.Quote(key.Name)} {kind} {clustering} ({FormatKeyColumns(key.Columns)})";
    }

    /// <summary>
    /// One <c>CREATE INDEX</c> against <paramref name="qualifiedTarget"/>. Returns null for an
    /// index kind that cannot be reconstructed from catalog metadata, having noted why.
    /// </summary>
    public static string? RenderIndex(string qualifiedTarget, TableIndexDefinition index, List<string> notes)
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
                    .Append(quotedName).Append(" ON ").Append(qualifiedTarget)
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
                return $"CREATE CLUSTERED COLUMNSTORE INDEX {quotedName} ON {qualifiedTarget};";

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

                return $"CREATE NONCLUSTERED COLUMNSTORE INDEX {quotedName} ON {qualifiedTarget} ({string.Join(", ", columns)});";
            }

            default:
                notes.Add($"Index {quotedName} ({index.TypeDesc}) was not scripted — only rowstore and columnstore indexes are reconstructed.");
                return null;
        }
    }

    /// <summary>
    /// One <c>ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY</c>. The referenced table is passed
    /// in separately so a sandbox copy's key points at the sandbox copy of its parent, never back
    /// at the original.
    /// </summary>
    public static string RenderForeignKey(
        string qualifiedTarget, TableForeignKeyDefinition fk, string referencedSchema, string referencedTable)
    {
        var sb = new StringBuilder("ALTER TABLE ").Append(qualifiedTarget)
            .Append(fk.IsNotTrusted ? " WITH NOCHECK" : " WITH CHECK")
            .Append(" ADD CONSTRAINT ").Append(SqlIdentifier.Quote(fk.Name))
            .Append(" FOREIGN KEY (")
            .Append(string.Join(", ", fk.Columns.Select(SqlIdentifier.Quote)))
            .Append(") REFERENCES ")
            .Append(SqlIdentifier.QuoteQualified(referencedSchema, referencedTable))
            .Append(" (")
            .Append(string.Join(", ", fk.ReferencedColumns.Select(SqlIdentifier.Quote)))
            .Append(')');

        if (!fk.DeleteAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON DELETE ").Append(fk.DeleteAction.Replace('_', ' '));
        if (!fk.UpdateAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase))
            sb.Append(" ON UPDATE ").Append(fk.UpdateAction.Replace('_', ' '));

        return sb.Append(';').ToString();
    }

    /// <summary>
    /// Renders a catalog type the way a <c>DECLARE</c> or a column definition needs it:
    /// <c>nvarchar(50)</c>, <c>nvarchar(max)</c>, <c>decimal(18,2)</c>, <c>datetime2(7)</c>, or a
    /// bare name for types without facets. Alias and table types render schema-qualified, since
    /// facets are fixed by the type itself.
    /// </summary>
    public static string RenderDataType(
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

    private static string FormatKeyColumns(IEnumerable<TableIndexColumn> columns) =>
        string.Join(", ", columns.Select(c => $"{SqlIdentifier.Quote(c.Column)} {(c.Descending ? "DESC" : "ASC")}"));

    /// <summary>Catalog expressions arrive already wrapped in parentheses; older builds occasionally do not.</summary>
    private static string Parenthesize(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? trimmed : $"({trimmed})";
    }

    // \z rather than $ so a trailing newline cannot smuggle anything past the COLLATE guard.
    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z", RegexOptions.Compiled)]
    private static partial Regex CollationNamePattern();
}
