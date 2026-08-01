using System.Data.Common;
using System.Globalization;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Shared plumbing for reading SQL Server catalog views. Catalog columns are typed
/// inconsistently — <c>tinyint</c>, <c>smallint</c>, <c>bit</c> and <c>sql_variant</c> all appear
/// where the caller just wants a number or a flag — so every read goes through one of these.
/// </summary>
internal static class CatalogRead
{
    /// <summary>Seconds a catalog query may take before it is abandoned.</summary>
    public const int CommandTimeoutSeconds = 120;

    public static DbCommand CreateCommand(DbConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        return cmd;
    }

    public static void AddParam(DbCommand cmd, string name, object value)
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
    public static int Int32Loose(DbDataReader reader, int ordinal)
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
    public static bool BooleanLoose(DbDataReader reader, int ordinal)
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
    public static string? VariantAsSqlLiteral(DbDataReader reader, int ordinal)
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

    /// <summary>Escapes a value for use inside a single-quoted T-SQL literal.</summary>
    public static string Literal(string value) => value.Replace("'", "''");
}
