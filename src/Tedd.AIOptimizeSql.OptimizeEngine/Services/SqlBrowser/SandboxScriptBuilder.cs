using System.Text;

using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Deterministic setup/teardown scaffolding for the two sandboxed isolation modes. What it
/// emits is correct but deliberately incomplete: <c>SELECT ... INTO</c> copies column shape and
/// data and nothing else, so every copy is followed by an explicit TODO checklist of the
/// physical design the AI (or the user) still has to recreate. Teardown is always safe to run
/// when setup never ran.
/// </summary>
public static class SandboxScriptBuilder
{
    /// <summary>Schema the sandbox copies live in when the wizard has no better suggestion.</summary>
    public const string DefaultSandboxSchema = "aiopt_sandbox";

    /// <summary>Suffix appended to the source database name to name a clone.</summary>
    public const string CloneDatabaseSuffix = "_aiopt_clone";

    /// <summary>
    /// Copies the included tables into <paramref name="sandboxSchema"/> in the same database.
    /// Views, procedures and functions the benchmark uses are not copied — rewriting them to
    /// point at the sandbox tables is a judgement call left to the AI, and the setup script says so.
    /// </summary>
    public static (string Setup, string Teardown) BuildSandboxSchema(
        string sandboxSchema,
        IReadOnlyList<BlueprintTable> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxSchema);
        ArgumentNullException.ThrowIfNull(tables);

        var included = tables.Where(t => t.Include).ToList();
        var quotedSchema = SqlIdentifier.Quote(sandboxSchema);

        var setup = new StringBuilder();
        setup.AppendLine("-- Sandbox setup for ExperimentIsolationMode.SandboxSchema.");
        setup.AppendLine("-- Runs once, before the experiment; the matching teardown drops everything again.");
        setup.AppendLine();
        setup.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'{Literal(sandboxSchema)}')");
        setup.AppendLine($"    EXEC (N'CREATE SCHEMA {quotedSchema};'); -- CREATE SCHEMA must be alone in its batch");
        setup.AppendLine();

        if (included.Count == 0)
        {
            setup.AppendLine("-- TODO(ai): no base tables were selected, so nothing is copied. Either include the");
            setup.AppendLine("--   tables the benchmark reads or drop back to isolation mode None.");
        }

        foreach (var table in included)
        {
            var source = SqlIdentifier.QuoteQualified(table.Schema, table.Table);
            var target = SqlIdentifier.QuoteQualified(sandboxSchema, table.Table);

            setup.AppendLine($"IF OBJECT_ID(N'{Literal(target)}', N'U') IS NOT NULL DROP TABLE {target};");
            setup.AppendLine($"SELECT * INTO {target} FROM {source};");
            AppendCopyChecklist(setup, source, target, table.RowCount);
            setup.AppendLine();
        }

        setup.AppendLine("-- TODO(ai): the benchmark's views, functions and procedures still point at the original");
        setup.AppendLine($"--   tables. Recreate the ones the benchmark touches inside {quotedSchema}, rewritten to");
        setup.AppendLine("--   reference the sandbox copies, and make the benchmark SQL call those copies -- otherwise");
        setup.AppendLine("--   the experiment measures the production objects and the sandbox is pointless.");

        var teardown = new StringBuilder();
        teardown.AppendLine("-- Sandbox teardown for ExperimentIsolationMode.SandboxSchema.");
        teardown.AppendLine("-- Idempotent: safe to run when setup never ran, or ran only partway.");
        teardown.AppendLine();

        foreach (var table in included)
        {
            var target = SqlIdentifier.QuoteQualified(sandboxSchema, table.Table);
            teardown.AppendLine($"IF OBJECT_ID(N'{Literal(target)}', N'U') IS NOT NULL DROP TABLE {target};");
        }

        teardown.AppendLine();
        teardown.AppendLine("-- TODO(ai): drop every other object setup created in the schema (views, procedures,");
        teardown.AppendLine("--   functions, types) before the DROP SCHEMA below -- it fails while the schema is not empty.");
        teardown.AppendLine();
        teardown.AppendLine($"IF EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'{Literal(sandboxSchema)}')");
        teardown.AppendLine($"    DROP SCHEMA {quotedSchema};");

        return (setup.ToString(), teardown.ToString());
    }

    /// <summary>
    /// Provisions <paramref name="sandboxDatabase"/> on the same server and copies the included
    /// tables into it from <paramref name="sourceDatabase"/>. The engine repoints the connection
    /// string's initial catalog at the clone for the duration of the experiment, so object names
    /// inside the benchmark keep working as long as every object exists in the clone.
    /// </summary>
    public static (string Setup, string Teardown) BuildCloneDatabase(
        string sandboxDatabase,
        string sourceDatabase,
        IReadOnlyList<BlueprintTable> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabase);
        ArgumentNullException.ThrowIfNull(tables);

        var included = tables.Where(t => t.Include).ToList();
        var quotedClone = SqlIdentifier.Quote(sandboxDatabase);

        var setup = new StringBuilder();
        setup.AppendLine("-- Sandbox setup for ExperimentIsolationMode.CloneDatabase.");
        setup.AppendLine("-- Must run against [master]: the clone cannot be created from inside itself.");
        setup.AppendLine();
        setup.AppendLine($"IF DB_ID(N'{Literal(sandboxDatabase)}') IS NULL");
        setup.AppendLine($"    CREATE DATABASE {quotedClone};");
        setup.AppendLine("GO"); // three-part names below are bound at compile time, so the database must already exist
        setup.AppendLine();

        foreach (var schema in included
                     .Select(t => t.Schema)
                     .Where(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            setup.AppendLine($"IF NOT EXISTS (SELECT 1 FROM {quotedClone}.sys.schemas WHERE [name] = N'{Literal(schema)}')");
            // CREATE SCHEMA must be alone in its batch and cannot cross databases, so it goes
            // through the clone's own sp_executesql.
            setup.AppendLine($"    EXEC {quotedClone}.sys.sp_executesql N'CREATE SCHEMA {Literal(SqlIdentifier.Quote(schema))};';");
        }

        setup.AppendLine();

        if (included.Count == 0)
        {
            setup.AppendLine("-- TODO(ai): no base tables were selected, so the clone stays empty. Either include the");
            setup.AppendLine("--   tables the benchmark reads or drop back to isolation mode None.");
            setup.AppendLine();
        }

        foreach (var table in included)
        {
            var source = $"{SqlIdentifier.Quote(sourceDatabase)}.{SqlIdentifier.QuoteQualified(table.Schema, table.Table)}";
            var target = $"{quotedClone}.{SqlIdentifier.QuoteQualified(table.Schema, table.Table)}";

            setup.AppendLine($"IF OBJECT_ID(N'{Literal(target)}', N'U') IS NOT NULL DROP TABLE {target};");
            setup.AppendLine($"SELECT * INTO {target} FROM {source};");
            AppendCopyChecklist(setup, source, target, table.RowCount);
            setup.AppendLine();
        }

        setup.AppendLine("-- TODO(ai): copy every non-table object the benchmark needs (views, functions, procedures,");
        setup.AppendLine("--   table types, synonyms, sequences) into the clone with its original definition, plus any");
        setup.AppendLine("--   lookup tables those objects read. A missing object fails the benchmark, it does not");
        setup.AppendLine("--   silently fall back to the source database.");

        var teardown = new StringBuilder();
        teardown.AppendLine("-- Sandbox teardown for ExperimentIsolationMode.CloneDatabase.");
        teardown.AppendLine("-- Must run against [master]; idempotent, and safe when setup never ran.");
        teardown.AppendLine();
        teardown.AppendLine($"IF DB_ID(N'{Literal(sandboxDatabase)}') IS NOT NULL");
        teardown.AppendLine("BEGIN");
        teardown.AppendLine($"    ALTER DATABASE {quotedClone} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        teardown.AppendLine($"    DROP DATABASE {quotedClone};");
        teardown.AppendLine("END");

        return (setup.ToString(), teardown.ToString());
    }

    /// <summary>Default clone database name for <paramref name="sourceDatabase"/>, clamped to sysname length.</summary>
    public static string DefaultCloneDatabaseName(string sourceDatabase)
    {
        var prefix = string.IsNullOrWhiteSpace(sourceDatabase) ? "aiopt" : sourceDatabase.Trim();
        var maxPrefix = 128 - CloneDatabaseSuffix.Length;
        if (prefix.Length > maxPrefix)
            prefix = prefix[..maxPrefix];
        return prefix + CloneDatabaseSuffix;
    }

    /// <summary>
    /// Spells out everything <c>SELECT ... INTO</c> silently drops. Left as comments so the AI
    /// has a checklist to fill in and a reviewer can see what is still missing if it did not.
    /// </summary>
    private static void AppendCopyChecklist(StringBuilder sb, string source, string target, long? sourceRowCount)
    {
        var rows = sourceRowCount.HasValue ? $" (source has {sourceRowCount.Value:N0} rows)" : "";
        sb.AppendLine($"-- TODO(ai): {target} is a heap copy of {source}{rows} with data and column");
        sb.AppendLine("--   types only. Recreate, sized for the copied row count:");
        sb.AppendLine("--     * PRIMARY KEY and UNIQUE constraints");
        sb.AppendLine("--     * the clustered index -- a SELECT ... INTO copy is always a heap");
        sb.AppendLine("--     * nonclustered indexes with their INCLUDE lists, filters and fill factor");
        sb.AppendLine("--     * columnstore indexes, clustered or nonclustered");
        sb.AppendLine("--     * DATA_COMPRESSION (ROW/PAGE) per index and per partition");
        sb.AppendLine("--     * partition function and scheme, or a deliberate note that the copy is not partitioned");
        sb.AppendLine("--     * IDENTITY, computed columns, DEFAULT and CHECK constraints -- all flattened by the copy");
        sb.AppendLine("--     * FOREIGN KEYs, pointing at the sandbox copies and never back at the originals");
        sb.AppendLine("--     * UPDATE STATISTICS ... WITH FULLSCAN once the indexes exist, so plans are comparable");
    }

    /// <summary>Escapes a value (or an already-built SQL fragment) for use inside a single-quoted T-SQL literal.</summary>
    private static string Literal(string value) => value.Replace("'", "''");
}
