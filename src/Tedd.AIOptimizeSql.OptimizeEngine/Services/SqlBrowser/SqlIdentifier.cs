namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Bracket-quoting for SQL Server identifiers. Every identifier this feature writes into
/// generated SQL goes through here, so a name containing <c>]</c> can never close the
/// quoting early and inject SQL.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>Wraps <paramref name="identifier"/> in brackets, doubling every <c>]</c> inside it.</summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return string.Concat("[", identifier.Replace("]", "]]"), "]");
    }

    /// <summary>
    /// Renders <c>[schema].[name]</c>. A blank schema falls back to <c>dbo</c>: generated SQL
    /// must never resolve through the executing login's default schema.
    /// </summary>
    public static string QuoteQualified(string schema, string name) =>
        string.Concat(Quote(string.IsNullOrWhiteSpace(schema) ? "dbo" : schema), ".", Quote(name));
}
