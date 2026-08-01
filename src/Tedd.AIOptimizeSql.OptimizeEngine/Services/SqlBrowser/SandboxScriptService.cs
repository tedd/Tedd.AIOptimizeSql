using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Agents.AI;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Generates the sandbox setup and teardown scripts from the live catalog. The scripts are meant
/// to run as-is: tables are recreated with the physical design measurements depend on and filled
/// from the source, and a clone database additionally gets the modules the benchmark calls, in
/// dependency order. Anything that could not be reproduced is reported as a warning rather than
/// left as a silent gap, because a sandbox that is quietly thinner than production makes every
/// measurement taken in it meaningless.
/// </summary>
public sealed partial class SandboxScriptService(
    ISchemaDiscoveryService schemaDiscovery,
    AiAgentFactory agentFactory,
    ILogger<SandboxScriptService> logger) : ISandboxScriptService
{
    /// <summary>Schema context is the bulk of the AI prompt; past this it is truncated with a note.</summary>
    private const int MaxSchemaContextChars = 40_000;

    /// <summary>Schemas a sandbox must never be built in, because teardown empties the schema.</summary>
    private static readonly HashSet<string> ReservedSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "dbo", "sys", "guest", "INFORMATION_SCHEMA" };

    private static readonly HashSet<string> SystemDatabases =
        new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

    #region Deterministic generation

    /// <inheritdoc />
    public async Task<SandboxScripts> GenerateAsync(
        string connectionString, SandboxScriptRequest request, CancellationToken ct = default)
    {
        var (scripts, _) = await GenerateCoreAsync(connectionString, request, ct);
        return scripts;
    }

    private async Task<(SandboxScripts Scripts, SandboxModel? Model)> GenerateCoreAsync(
        string connectionString, SandboxScriptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (request.IsolationMode == ExperimentIsolationMode.None)
            return (SandboxScripts.NotIsolated, null);

        var model = await LoadAsync(connectionString, request, ct);

        var refusal = Refuse(request, model);
        if (refusal is not null)
        {
            logger.LogWarning("Sandbox generation refused for isolation mode {Mode}: {Reason}",
                request.IsolationMode, refusal);
            return (new SandboxScripts
            {
                Warnings = [.. model.Warnings, refusal],
                Summary = "No scripts were generated."
            }, model);
        }

        var (setup, teardown) = request.IsolationMode == ExperimentIsolationMode.CloneDatabase
            ? BuildCloneDatabaseScripts(request.SandboxDatabaseName!, model)
            : BuildSandboxSchemaScripts(request.SandboxSchemaName!, model);

        logger.LogInformation(
            "Generated {Mode} sandbox scripts: {Tables} table(s), {Modules} module(s), {Warnings} warning(s)",
            request.IsolationMode, model.Tables.Count, model.Modules.Count, model.Warnings.Count);

        return (new SandboxScripts
        {
            Setup = setup,
            Teardown = teardown,
            Warnings = model.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Summary = BuildSummary(request, model)
        }, model);
    }

    private static string BuildSummary(SandboxScriptRequest request, SandboxModel model)
    {
        var rows = model.Tables.Count == 0
            ? "no tables"
            : $"{model.Tables.Count} table(s)";
        var modules = model.Modules.Count > 0 ? $", {model.Modules.Count} module(s)" : "";
        var target = request.IsolationMode == ExperimentIsolationMode.CloneDatabase
            ? $"database [{request.SandboxDatabaseName}]"
            : $"schema [{request.SandboxSchemaName}]";

        return $"Generated setup and teardown for {target}: {rows}{modules} copied from [{model.SourceDatabase}].";
    }

    /// <summary>
    /// The one place that says no. Teardown empties whatever it is pointed at, so a sandbox name
    /// that collides with something real is refused outright rather than warned about.
    /// </summary>
    internal static string? Refuse(SandboxScriptRequest request, SandboxModel model)
    {
        if (request.IsolationMode == ExperimentIsolationMode.CloneDatabase)
        {
            var name = request.SandboxDatabaseName;
            if (string.IsNullOrWhiteSpace(name))
                return "No sandbox database name is set, so no clone scripts could be generated.";
            if (SystemDatabases.Contains(name))
                return $"[{name}] is a system database. Pick a name that does not exist yet — teardown drops whatever it is pointed at.";
            if (string.Equals(name, model.SourceDatabase, StringComparison.OrdinalIgnoreCase))
                return $"The sandbox database name is the same as the source database [{model.SourceDatabase}]. The clone must be a separate database.";
            return null;
        }

        var schema = request.SandboxSchemaName;
        if (string.IsNullOrWhiteSpace(schema))
            return "No sandbox schema name is set, so no sandbox scripts could be generated.";
        if (ReservedSchemas.Contains(schema) || schema.StartsWith("db_", StringComparison.OrdinalIgnoreCase))
            return $"[{schema}] is a built-in schema. Pick a dedicated sandbox schema — teardown drops every object in the schema it is given.";
        if (model.ExistingObjectsInSandboxSchema > 0)
            return $"Schema [{schema}] already exists and holds {model.ExistingObjectsInSandboxSchema} object(s). " +
                   "Teardown drops everything in the sandbox schema, so this refuses rather than risk dropping something " +
                   "that is not ours: run the existing teardown script first if a previous run left it behind, or pick a " +
                   "name that is not in use.";

        return null;
    }

    #endregion

    #region Catalog model

    internal sealed record SandboxModel
    {
        public required string SourceDatabase { get; init; }
        public required IReadOnlyList<TableDefinition> Tables { get; init; }
        public required IReadOnlyList<ModuleDefinition> Modules { get; init; }
        public required List<string> Warnings { get; init; }
        public required string SchemaContextMarkdown { get; init; }
        public int ExistingObjectsInSandboxSchema { get; init; }

        /// <summary>Lookup of the tables that get a sandbox copy, for retargeting foreign keys.</summary>
        public HashSet<string> CopiedTableKeys =>
            new(Tables.Select(t => Key(t.Schema, t.Table)), StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record ModuleDefinition(string Schema, string Name, SqlObjectKind Kind, string Definition);

    private async Task<SandboxModel> LoadAsync(
        string connectionString, SandboxScriptRequest request, CancellationToken ct)
    {
        var warnings = new List<string>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var included = request.Tables.Where(t => t.Include).ToList();
        if (included.Count == 0)
        {
            warnings.Add(
                "No base tables are ticked, so the sandbox would be empty. Include the tables the benchmark " +
                "reads, or drop back to isolation mode None.");
        }

        var tables = new List<TableDefinition>();
        foreach (var table in included)
        {
            var definition = await TableDefinitionReader.ReadAsync(conn, table.Schema, table.Table, ct);
            if (definition is null)
            {
                warnings.Add(
                    $"{SqlIdentifier.QuoteQualified(table.Schema, table.Table)} could not be read from the catalog " +
                    "(it does not exist, or the login cannot see its columns) and was not copied.");
                continue;
            }

            tables.Add(definition);
        }

        var modules = new List<ModuleDefinition>();
        var schemaContext = "";
        if (string.IsNullOrWhiteSpace(request.BenchmarkSql))
        {
            warnings.Add(
                "No benchmark SQL was available, so only the listed tables were copied. Any view, function or " +
                "procedure the benchmark calls has to be added to the setup script by hand.");
        }
        else
        {
            try
            {
                var discovery = await schemaDiscovery.DiscoverSqlContextAsync(request.BenchmarkSql, conn, ct);
                schemaContext = discovery.MarkdownSummary;
                modules.AddRange(CollectModules(discovery, warnings));
                WarnAboutUncopiedDependencies(discovery, tables, warnings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dependency discovery failed while generating sandbox scripts");
                warnings.Add(
                    $"The benchmark's dependencies could not be discovered ({ex.Message}), so only the listed " +
                    "tables were copied. Check that every view, function and procedure it calls exists in the sandbox.");
            }
        }

        var existingObjects = request.IsolationMode == ExperimentIsolationMode.SandboxSchema
                              && !string.IsNullOrWhiteSpace(request.SandboxSchemaName)
            ? await CountObjectsInSchemaAsync(conn, request.SandboxSchemaName!, ct)
            : 0;

        return new SandboxModel
        {
            SourceDatabase = conn.Database,
            Tables = tables,
            Modules = modules,
            Warnings = warnings,
            SchemaContextMarkdown = schemaContext,
            ExistingObjectsInSandboxSchema = existingObjects
        };
    }

    private static async Task<int> CountObjectsInSchemaAsync(
        SqlConnection conn, string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT_BIG(*)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE s.[name] = @schema AND o.is_ms_shipped = 0
            """;

        await using var cmd = CatalogRead.CreateCommand(conn, sql);
        CatalogRead.AddParam(cmd, "@schema", schema);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : checked((int)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The modules the benchmark reaches, ordered so a module is created after everything it
    /// references. Encrypted and unreadable definitions are reported instead of copied.
    /// </summary>
    private static List<ModuleDefinition> CollectModules(SchemaDiscoveryResult discovery, List<string> warnings)
    {
        var modules = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in discovery.Objects)
        {
            if (!IsCopyableModuleKind(o.Kind))
            {
                if (o.Kind is SqlObjectKind.Synonym or SqlObjectKind.Sequence or SqlObjectKind.TableType)
                {
                    warnings.Add(
                        $"{SqlIdentifier.QuoteQualified(o.Schema, o.Name)} is a {Describe(o.Kind)} and was not copied — " +
                        "the generator only reproduces tables and stored module definitions. Add it to the setup script by hand.");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(o.Definition))
            {
                warnings.Add(
                    $"{SqlIdentifier.QuoteQualified(o.Schema, o.Name)} ({Describe(o.Kind)}) has no readable definition " +
                    $"({(o.IsEncrypted ? "it is encrypted" : "the login may lack VIEW DEFINITION")}) and was not copied.");
                continue;
            }

            modules[Key(o.Schema, o.Name)] = new ModuleDefinition(o.Schema, o.Name, o.Kind, o.Definition!);
        }

        var dependsOn = modules.Keys.ToDictionary(
            k => k, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in discovery.Dependencies)
        {
            var from = Key(edge.ReferencingSchema, edge.ReferencingName);
            var to = Key(edge.ReferencedSchema, edge.ReferencedName);
            if (dependsOn.ContainsKey(from) && modules.ContainsKey(to) &&
                !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                dependsOn[from].Add(to);
            }
        }

        var ordered = new List<ModuleDefinition>(modules.Count);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sawCycle = false;

        void Visit(string key)
        {
            if (state.TryGetValue(key, out var visitState))
            {
                if (visitState == 1)
                    sawCycle = true;
                return;
            }

            state[key] = 1;
            foreach (var dependency in dependsOn[key].OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                Visit(dependency);
            state[key] = 2;
            ordered.Add(modules[key]);
        }

        foreach (var key in modules.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            Visit(key);

        if (sawCycle)
        {
            warnings.Add(
                "The benchmark's modules reference each other circularly, so the create order in the setup " +
                "script is a guess. If a CREATE fails on a missing object, reorder that block.");
        }

        return ordered;
    }

    /// <summary>
    /// Warns about tables a copied module reads that are not themselves copied: in the sandbox
    /// those references either fail outright or, worse, resolve back to production.
    /// </summary>
    private static void WarnAboutUncopiedDependencies(
        SchemaDiscoveryResult discovery, IReadOnlyList<TableDefinition> copied, List<string> warnings)
    {
        var copiedKeys = new HashSet<string>(
            copied.Select(t => Key(t.Schema, t.Table)), StringComparer.OrdinalIgnoreCase);

        var missing = discovery.BaseTables
            .Where(t => !copiedKeys.Contains(Key(t.Schema, t.Table)))
            .Select(t => SqlIdentifier.QuoteQualified(t.Schema, t.Table))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
            return;

        warnings.Add(
            $"The benchmark also reaches {string.Join(", ", missing.Take(10))}" +
            (missing.Count > 10 ? $" and {missing.Count - 10} more" : "") +
            ", which are not ticked and so are not copied. Tick them, or expect the sandbox to fail on a missing table.");
    }

    private static bool IsCopyableModuleKind(SqlObjectKind kind) => kind
        is SqlObjectKind.View
        or SqlObjectKind.StoredProcedure
        or SqlObjectKind.ScalarFunction
        or SqlObjectKind.TableValuedFunction
        or SqlObjectKind.InlineTableValuedFunction
        or SqlObjectKind.Trigger;

    private static string Describe(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.View => "view",
        SqlObjectKind.StoredProcedure => "stored procedure",
        SqlObjectKind.ScalarFunction or SqlObjectKind.TableValuedFunction or SqlObjectKind.InlineTableValuedFunction => "function",
        SqlObjectKind.Trigger => "trigger",
        SqlObjectKind.Synonym => "synonym",
        SqlObjectKind.Sequence => "sequence",
        SqlObjectKind.TableType => "table type",
        SqlObjectKind.Table => "table",
        _ => "object"
    };

    #endregion

    #region Clone database

    /// <summary>
    /// Scripts a clone of the source database. Names inside the clone are identical to the
    /// originals, so module definitions are copied verbatim and the benchmark needs no rewriting —
    /// the engine simply repoints the connection's initial catalog at the clone.
    /// </summary>
    internal static (string Setup, string Teardown) BuildCloneDatabaseScripts(string cloneDatabase, SandboxModel model)
    {
        var clone = SqlIdentifier.Quote(cloneDatabase);
        var source = SqlIdentifier.Quote(model.SourceDatabase);
        var copied = model.CopiedTableKeys;

        var setup = new StringBuilder();
        setup.AppendLine("-- Sandbox setup for ExperimentIsolationMode.CloneDatabase.");
        setup.AppendLine($"-- Generated from the live catalog of {source}; every object below is a real copy.");
        setup.AppendLine("-- Runs against [master]: a database cannot be created from inside itself.");
        setup.AppendLine("-- The clone is disposable and re-provisioned from scratch, so this is safe to re-run.");
        setup.AppendLine();
        setup.AppendLine($"IF DB_ID(N'{Lit(cloneDatabase)}') IS NOT NULL");
        setup.AppendLine("BEGIN");
        setup.AppendLine($"    ALTER DATABASE {clone} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        setup.AppendLine($"    DROP DATABASE {clone};");
        setup.AppendLine("END");
        setup.AppendLine($"CREATE DATABASE {clone};");
        // Three-part names below are bound when the batch compiles, so the clone must exist first.
        setup.AppendLine("GO");
        setup.AppendLine();

        var schemas = model.Tables.Select(t => t.Schema)
            .Concat(model.Modules.Select(m => m.Schema))
            .Where(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (schemas.Count > 0)
        {
            setup.AppendLine("-- Schemas");
            foreach (var schema in schemas)
            {
                // CREATE SCHEMA must be alone in its batch and cannot cross databases, so it goes
                // through the clone's own sp_executesql.
                setup.AppendLine(InClone(clone,
                    $"IF SCHEMA_ID(N'{Lit(schema)}') IS NULL EXEC (N'CREATE SCHEMA {SqlIdentifier.Quote(schema)};');"));
            }

            setup.AppendLine("GO");
            setup.AppendLine();
        }

        var notes = new List<string>();

        foreach (var table in model.Tables)
        {
            var qualified = SqlIdentifier.QuoteQualified(table.Schema, table.Table);
            var target = $"{clone}.{qualified}";
            var sourceTable = $"{source}.{qualified}";

            setup.AppendLine($"-- {qualified}");
            setup.AppendLine(InClone(clone, TableScriptWriter.RenderCreateTable(table, table.Schema, table.Table, notes)));
            setup.AppendLine("GO");
            AppendDataCopy(setup, table, target, sourceTable, model.Warnings);
            setup.AppendLine("GO");

            foreach (var index in table.Indexes)
            {
                var script = TableScriptWriter.RenderIndex(qualified, index, notes);
                if (script is not null)
                    setup.AppendLine(InClone(clone, script));
            }

            setup.AppendLine(InClone(clone, $"UPDATE STATISTICS {qualified} WITH FULLSCAN;"));
            setup.AppendLine("GO");
            setup.AppendLine();
        }

        AppendForeignKeys(setup, model, copied,
            statement => InClone(clone, statement),
            (schema, table) => (schema, table));

        if (model.Modules.Count > 0)
        {
            setup.AppendLine("-- Views, functions, procedures and triggers the benchmark calls, in dependency order.");
            setup.AppendLine("-- Definitions are copied verbatim: inside the clone every name resolves as it does in the source.");
            foreach (var module in model.Modules)
            {
                setup.AppendLine(InClone(clone, DropModuleStatement(module)));
                setup.AppendLine("GO");
                setup.AppendLine(InCloneModule(clone, module.Definition));
                setup.AppendLine("GO");
            }

            setup.AppendLine();
        }

        AppendNotes(setup, notes, model.Warnings);

        var teardown = new StringBuilder();
        teardown.AppendLine("-- Sandbox teardown for ExperimentIsolationMode.CloneDatabase.");
        teardown.AppendLine("-- Runs against [master]; idempotent, and safe when setup never ran.");
        teardown.AppendLine();
        teardown.AppendLine($"IF DB_ID(N'{Lit(cloneDatabase)}') IS NOT NULL");
        teardown.AppendLine("BEGIN");
        teardown.AppendLine($"    ALTER DATABASE {clone} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        teardown.AppendLine($"    DROP DATABASE {clone};");
        teardown.AppendLine("END");

        return (setup.ToString(), teardown.ToString());
    }

    /// <summary>Wraps one statement so it executes inside the clone rather than in [master].</summary>
    private static string InClone(string quotedClone, string statement) =>
        $"EXEC {quotedClone}.sys.sp_executesql N'{Lit(statement)}';";

    /// <summary>
    /// Same as <see cref="InClone"/>, but a module definition is user-written and may contain a
    /// line consisting only of <c>GO</c> (commented-out code, usually). The generated script is
    /// split on those lines before it runs, which would cut the definition in half, so such lines
    /// are carried across as a placeholder and restored at run time. <c>sp_executesql</c> refuses
    /// an expression for its statement, hence the variable.
    /// </summary>
    private static string InCloneModule(string quotedClone, string definition)
    {
        var masked = StandaloneGoPattern().Replace(definition, "${lead}" + GoPlaceholder + "${trail}");
        if (string.Equals(masked, definition, StringComparison.Ordinal))
            return InClone(quotedClone, definition);

        return $"DECLARE @aiopt_module nvarchar(max) = REPLACE(N'{Lit(masked)}', N'{GoPlaceholder}', N'GO');{Nl}" +
               $"EXEC {quotedClone}.sys.sp_executesql @aiopt_module;";
    }

    private const string GoPlaceholder = "~~aiopt~batch~separator~~";

    private static string DropModuleStatement(ModuleDefinition module)
    {
        var qualified = SqlIdentifier.QuoteQualified(module.Schema, module.Name);
        var (keyword, typeCodes) = module.Kind switch
        {
            SqlObjectKind.View => ("VIEW", "'V'"),
            SqlObjectKind.StoredProcedure => ("PROCEDURE", "'P','PC'"),
            SqlObjectKind.Trigger => ("TRIGGER", "'TR'"),
            _ => ("FUNCTION", "'FN','FS','IF','TF','FT'")
        };

        return $"IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'{Lit(qualified)}') " +
               $"AND [type] IN ({typeCodes})) DROP {keyword} {qualified};";
    }

    #endregion

    #region Sandbox schema

    /// <summary>
    /// Scripts copies of the benchmark's tables into one schema of the same database. Modules are
    /// deliberately not copied here: their bodies name the original tables, and rewriting a body
    /// to point at the sandbox is a judgement call the AI pass makes, not a mechanical one.
    /// </summary>
    internal static (string Setup, string Teardown) BuildSandboxSchemaScripts(string sandboxSchema, SandboxModel model)
    {
        var quotedSchema = SqlIdentifier.Quote(sandboxSchema);
        var copied = model.CopiedTableKeys;
        var notes = new List<string>();

        var setup = new StringBuilder();
        setup.AppendLine("-- Sandbox setup for ExperimentIsolationMode.SandboxSchema.");
        setup.AppendLine($"-- Generated from the live catalog of [{Lit(model.SourceDatabase)}]; runs against that database.");
        setup.AppendLine("-- Idempotent: it clears the sandbox schema first, so it is safe to re-run.");
        setup.AppendLine();
        setup.AppendLine("-- Clear anything a previous run left behind.");
        setup.Append(BuildSchemaCleanupBlock(sandboxSchema, dropSchema: false));
        setup.AppendLine("GO");
        setup.AppendLine();
        setup.AppendLine($"IF SCHEMA_ID(N'{Lit(sandboxSchema)}') IS NULL");
        // CREATE SCHEMA must be alone in its batch.
        setup.AppendLine($"    EXEC (N'CREATE SCHEMA {quotedSchema};');");
        setup.AppendLine("GO");
        setup.AppendLine();

        foreach (var table in model.Tables)
        {
            var target = SqlIdentifier.QuoteQualified(sandboxSchema, table.Table);
            var sourceTable = SqlIdentifier.QuoteQualified(table.Schema, table.Table);

            setup.AppendLine($"-- {sourceTable} -> {target}");
            setup.AppendLine(TableScriptWriter.RenderCreateTable(table, sandboxSchema, table.Table, notes));
            setup.AppendLine("GO");
            AppendDataCopy(setup, table, target, sourceTable, model.Warnings);

            foreach (var index in table.Indexes)
            {
                var script = TableScriptWriter.RenderIndex(target, index, notes);
                if (script is not null)
                    setup.AppendLine(script);
            }

            setup.AppendLine($"UPDATE STATISTICS {target} WITH FULLSCAN;");
            setup.AppendLine("GO");
            setup.AppendLine();
        }

        AppendForeignKeys(setup, model, copied,
            statement => statement,
            (_, table) => (sandboxSchema, table));

        if (model.Tables.Count > 1)
        {
            // Two tables of the same name in different source schemas would collide in the flat
            // sandbox schema; say so rather than emitting a script whose second CREATE fails.
            var collisions = model.Tables
                .GroupBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var name in collisions)
            {
                model.Warnings.Add(
                    $"More than one source schema has a table called [{name}]. The sandbox schema is flat, so the " +
                    "copies collide — use the clone-database mode for this experiment instead.");
            }
        }

        if (model.Modules.Count > 0)
        {
            setup.AppendLine("-- The benchmark also calls:");
            foreach (var module in model.Modules)
                setup.AppendLine($"--   {SqlIdentifier.QuoteQualified(module.Schema, module.Name)} ({Describe(module.Kind)})");
            setup.AppendLine("-- Their bodies still name the original tables, so as written they would measure");
            setup.AppendLine($"-- production. Recreate them inside {quotedSchema} against the copies above, and point the");
            setup.AppendLine("-- benchmark at the copies -- or use the clone-database mode, where names do not change.");
            setup.AppendLine();

            model.Warnings.Add(
                $"{model.Modules.Count} view/function/procedure the benchmark calls were not copied into " +
                $"[{sandboxSchema}]: their bodies name the original tables. Use \"Complete with AI\" to have them " +
                "rewritten against the sandbox copies, or switch to the clone-database mode where names stay the same.");
        }

        AppendNotes(setup, notes, model.Warnings);

        var teardown = new StringBuilder();
        teardown.AppendLine("-- Sandbox teardown for ExperimentIsolationMode.SandboxSchema.");
        teardown.AppendLine("-- Drops everything in the sandbox schema, including anything added after setup ran,");
        teardown.AppendLine("-- then the schema itself. Idempotent, and safe when setup never ran.");
        teardown.AppendLine();
        teardown.Append(BuildSchemaCleanupBlock(sandboxSchema, dropSchema: true));

        return (setup.ToString(), teardown.ToString());
    }

    /// <summary>
    /// Empties a schema in dependency-safe order — foreign keys, then modules, then tables, then
    /// table types — using the catalog rather than a fixed list, so an object the AI or the user
    /// added later is still removed. <c>STRING_AGG</c> needs SQL Server 2017 or later.
    /// </summary>
    private static string BuildSchemaCleanupBlock(string sandboxSchema, bool dropSchema)
    {
        var literal = Lit(sandboxSchema);
        var sb = new StringBuilder();

        sb.AppendLine("DECLARE @aiopt_cleanup nvarchar(max);");
        sb.AppendLine();
        sb.AppendLine("-- Foreign keys first: they pin the tables they point at.");
        sb.AppendLine("SELECT @aiopt_cleanup = STRING_AGG(CAST(");
        sb.AppendLine("        N'ALTER TABLE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name]) +");
        sb.AppendLine("        N' DROP CONSTRAINT ' + QUOTENAME(fk.[name]) + N';' AS nvarchar(max)), CHAR(10))");
        sb.AppendLine("FROM sys.foreign_keys fk");
        sb.AppendLine("JOIN sys.tables t ON t.object_id = fk.parent_object_id");
        sb.AppendLine("JOIN sys.schemas s ON s.schema_id = t.schema_id");
        sb.AppendLine($"WHERE s.[name] = N'{literal}';");
        sb.AppendLine("IF @aiopt_cleanup IS NOT NULL EXEC sys.sp_executesql @aiopt_cleanup;");
        sb.AppendLine();
        sb.AppendLine("-- Modules next: a view or function can be schema-bound to a table below.");
        sb.AppendLine("SELECT @aiopt_cleanup = STRING_AGG(CAST(N'DROP ' + CASE o.[type]");
        sb.AppendLine("        WHEN 'V'  THEN N'VIEW'");
        sb.AppendLine("        WHEN 'P'  THEN N'PROCEDURE'");
        sb.AppendLine("        WHEN 'PC' THEN N'PROCEDURE'");
        sb.AppendLine("        WHEN 'SN' THEN N'SYNONYM'");
        sb.AppendLine("        WHEN 'SO' THEN N'SEQUENCE'");
        sb.AppendLine("        ELSE N'FUNCTION' END +");
        sb.AppendLine("        N' ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(o.[name]) + N';' AS nvarchar(max)), CHAR(10))");
        sb.AppendLine("FROM sys.objects o");
        sb.AppendLine("JOIN sys.schemas s ON s.schema_id = o.schema_id");
        sb.AppendLine($"WHERE s.[name] = N'{literal}'");
        sb.AppendLine("  AND o.[type] IN ('V','P','PC','FN','FS','IF','TF','FT','SN','SO');");
        sb.AppendLine("IF @aiopt_cleanup IS NOT NULL EXEC sys.sp_executesql @aiopt_cleanup;");
        sb.AppendLine();
        sb.AppendLine("-- Then the tables, and the table types that would block DROP SCHEMA.");
        sb.AppendLine("SELECT @aiopt_cleanup = STRING_AGG(CAST(");
        sb.AppendLine("        N'DROP TABLE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name]) + N';' AS nvarchar(max)), CHAR(10))");
        sb.AppendLine("FROM sys.tables t");
        sb.AppendLine("JOIN sys.schemas s ON s.schema_id = t.schema_id");
        sb.AppendLine($"WHERE s.[name] = N'{literal}';");
        sb.AppendLine("IF @aiopt_cleanup IS NOT NULL EXEC sys.sp_executesql @aiopt_cleanup;");
        sb.AppendLine();
        sb.AppendLine("SELECT @aiopt_cleanup = STRING_AGG(CAST(");
        sb.AppendLine("        N'DROP TYPE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(tt.[name]) + N';' AS nvarchar(max)), CHAR(10))");
        sb.AppendLine("FROM sys.table_types tt");
        sb.AppendLine("JOIN sys.schemas s ON s.schema_id = tt.schema_id");
        sb.AppendLine($"WHERE s.[name] = N'{literal}';");
        sb.AppendLine("IF @aiopt_cleanup IS NOT NULL EXEC sys.sp_executesql @aiopt_cleanup;");

        if (dropSchema)
        {
            sb.AppendLine();
            sb.AppendLine($"IF SCHEMA_ID(N'{literal}') IS NOT NULL");
            sb.AppendLine($"    EXEC (N'DROP SCHEMA {SqlIdentifier.Quote(sandboxSchema)};');");
        }

        return sb.ToString();
    }

    #endregion

    #region Shared script fragments

    /// <summary>
    /// Copies the rows. Computed and rowversion columns are left out — the copy regenerates them —
    /// and an identity column needs IDENTITY_INSERT so the keys the benchmark filters on survive.
    /// </summary>
    private static void AppendDataCopy(
        StringBuilder sb, TableDefinition table, string target, string source, List<string> warnings)
    {
        var columns = table.CopyableColumns;
        if (columns.Count == 0)
        {
            warnings.Add(
                $"{source} has no columns that can be inserted (every column is computed or a rowversion), " +
                "so the copy is left empty.");
            sb.AppendLine($"-- {source} has no insertable columns; the copy stays empty.");
            return;
        }

        var list = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name)));

        if (table.HasIdentity)
            sb.AppendLine($"SET IDENTITY_INSERT {target} ON;");
        sb.AppendLine($"INSERT INTO {target} ({list})");
        sb.AppendLine($"SELECT {list} FROM {source};");
        if (table.HasIdentity)
            sb.AppendLine($"SET IDENTITY_INSERT {target} OFF;");
    }

    /// <summary>
    /// Foreign keys come last, once every table holds its rows. A key whose parent is not copied
    /// is dropped rather than pointed back at the original: a sandbox row must never be able to
    /// depend on a production row.
    /// </summary>
    private static void AppendForeignKeys(
        StringBuilder sb,
        SandboxModel model,
        HashSet<string> copied,
        Func<string, string> wrap,
        Func<string, string, (string Schema, string Table)> retarget)
    {
        var statements = new List<string>();

        foreach (var table in model.Tables)
        {
            var (targetSchema, targetTable) = retarget(table.Schema, table.Table);
            var target = SqlIdentifier.QuoteQualified(targetSchema, targetTable);

            foreach (var fk in table.ForeignKeys)
            {
                if (!copied.Contains(Key(fk.ReferencedSchema, fk.ReferencedTable)))
                {
                    model.Warnings.Add(
                        $"Foreign key [{fk.Name}] on {SqlIdentifier.QuoteQualified(table.Schema, table.Table)} points at " +
                        $"{SqlIdentifier.QuoteQualified(fk.ReferencedSchema, fk.ReferencedTable)}, which is not copied, so the " +
                        "constraint was left out. Tick that table if the optimizer's plans depend on the key.");
                    continue;
                }

                var (referencedSchema, referencedTable) = retarget(fk.ReferencedSchema, fk.ReferencedTable);
                statements.Add(wrap(TableScriptWriter.RenderForeignKey(target, fk, referencedSchema, referencedTable)));

                if (fk.IsDisabled)
                {
                    statements.Add(wrap(
                        $"ALTER TABLE {target} NOCHECK CONSTRAINT {SqlIdentifier.Quote(fk.Name)};"));
                }
            }
        }

        if (statements.Count == 0)
            return;

        sb.AppendLine("-- Foreign keys, once every table is loaded.");
        foreach (var statement in statements)
            sb.AppendLine(statement);
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    /// <summary>
    /// Fidelity gaps the script writer found (an index kind it cannot reconstruct, an unreadable
    /// computed column) go into the script as comments and into the warning list, so they are
    /// visible whether the user reads the SQL or the summary.
    /// </summary>
    private static void AppendNotes(StringBuilder sb, List<string> notes, List<string> warnings)
    {
        if (notes.Count == 0)
            return;

        sb.AppendLine("-- Not everything could be reproduced from catalog metadata:");
        foreach (var note in notes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine("--   " + note.Replace('\r', ' ').Replace('\n', ' '));
            warnings.Add(note);
        }
    }

    #endregion

    #region AI refinement

    /// <inheritdoc />
    public async Task<SandboxScripts> GenerateWithAiAsync(
        AIConnection aiConnection,
        string connectionString,
        SandboxScriptRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aiConnection);

        progress?.Report("Reading the catalog…");
        var (generated, model) = await GenerateCoreAsync(connectionString, request, ct);

        if (model is null || generated.Setup is null)
            return generated;

        var warnings = generated.Warnings.ToList();

        try
        {
            progress?.Report($"Asking {aiConnection.Model} to finish the sandbox scripts…");

            var agent = agentFactory.Create(aiConnection, AgentInstructions, new List<AITool>());
            var sw = Stopwatch.StartNew();
            var response = await agent.RunAsync(BuildPrompt(request, model, generated), cancellationToken: ct);
            sw.Stop();

            progress?.Report($"AI responded after {sw.ElapsedMilliseconds} ms, parsing…");

            var parsed = Parse(response?.ToString());
            if (parsed is null)
            {
                warnings.Add(
                    "The AI reply could not be parsed as JSON, so the deterministic scripts were kept unchanged.");
                return generated with { Warnings = Dedupe(warnings) };
            }

            var setup = FirstNonBlank(parsed.Sandbox_setup_sql, generated.Setup);
            var teardown = FirstNonBlank(parsed.Sandbox_teardown_sql, generated.Teardown);

            if (parsed.Warnings is { Count: > 0 })
                warnings.AddRange(parsed.Warnings.Where(w => !string.IsNullOrWhiteSpace(w))!);

            logger.LogInformation("AI refined the sandbox scripts in {ElapsedMs} ms", sw.ElapsedMilliseconds);

            return generated with
            {
                Setup = setup,
                Teardown = teardown,
                Warnings = Dedupe(warnings),
                Summary = generated.Summary + $" Reviewed and completed by {aiConnection.Model}."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI refinement of the sandbox scripts failed");
            warnings.Add(
                $"The AI could not be used to finish these scripts ({ex.Message}). The deterministic scripts " +
                "below are unchanged and still runnable, but any gap listed above is still a gap.");
            return generated with { Warnings = Dedupe(warnings) };
        }
    }

    private const string AgentInstructions = """
        You are a SQL Server performance engineer preparing an isolated sandbox for a benchmark.

        The sandbox exists so a query can be optimized without touching production data, and so
        measurements taken in it mean something. A copy that is missing an index, a constraint or a
        compression setting produces plans that do not match production, which is worse than no
        sandbox at all.

        Rules, without exception:
        * T-SQL for Microsoft SQL Server only. No other dialect, no PowerShell, no shell.
        * Every identifier you emit is bracket-quoted and schema-qualified: [dbo].[Orders].
        * Scripts stay idempotent and safe to re-run, and teardown must succeed when setup never ran.
        * Nothing in either script may modify, delete or reorganize the source objects. Sandbox
          scripts read the originals and write only to the sandbox.
        * Keep the generated structure and its GO batch separators. Change what is wrong or missing;
          do not rewrite what already works.
        * Never invent syntax. If you are unsure a construct parses, leave the generated version
          alone and explain the gap in "warnings".
        """;

    private static string BuildPrompt(SandboxScriptRequest request, SandboxModel model, SandboxScripts generated)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Sandbox scripts to finish");
        sb.AppendLine();
        sb.AppendLine($"- Isolation mode: {request.IsolationMode}");
        sb.AppendLine($"- Source database: [{model.SourceDatabase}]");
        if (request.IsolationMode == ExperimentIsolationMode.SandboxSchema)
            sb.AppendLine($"- Sandbox schema: [{request.SandboxSchemaName}] (same database as the source)");
        else
            sb.AppendLine($"- Sandbox database: [{request.SandboxDatabaseName}] (same server; setup and teardown run against [master])");
        sb.AppendLine();

        sb.AppendLine("The scripts below were generated from catalog metadata and already recreate the tables with");
        sb.AppendLine("their real physical design, load their rows, and rebuild statistics. Your job is only the");
        sb.AppendLine("parts metadata cannot decide. Return both scripts complete, keeping everything that is");
        sb.AppendLine("already correct.");
        sb.AppendLine();

        if (model.Warnings.Count > 0)
        {
            sb.AppendLine("## Gaps the generator reported");
            sb.AppendLine();
            foreach (var warning in model.Warnings.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        if (request.IsolationMode == ExperimentIsolationMode.SandboxSchema && model.Modules.Count > 0)
        {
            sb.AppendLine("## The modules that still have to be repointed");
            sb.AppendLine();
            sb.AppendLine("Each definition below names the original tables. Recreate it inside the sandbox schema,");
            sb.AppendLine("rewritten so every reference to a copied table resolves to the copy, and say in \"warnings\"");
            sb.AppendLine("what the benchmark must now call instead of the original name.");
            sb.AppendLine();
            foreach (var module in model.Modules)
            {
                sb.AppendLine($"### {SqlIdentifier.QuoteQualified(module.Schema, module.Name)} ({Describe(module.Kind)})");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(module.Definition);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Benchmark SQL");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(string.IsNullOrWhiteSpace(request.BenchmarkSql) ? "(not available)" : request.BenchmarkSql);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Schema context (deterministic catalog discovery)");
        sb.AppendLine();
        sb.AppendLine(Truncate(model.SchemaContextMarkdown, MaxSchemaContextChars));
        sb.AppendLine();

        sb.AppendLine("## Generated setup");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(generated.Setup);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Generated teardown");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(generated.Teardown);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Response format");
        sb.AppendLine();
        sb.AppendLine("""
            Reply with exactly one JSON object and nothing else: no prose before or after, no markdown
            code fence. Every SQL field is a single string that may contain newlines.

            {
              "sandbox_setup_sql": "the complete setup script",
              "sandbox_teardown_sql": "the complete teardown script",
              "warnings": ["anything the user must check before running"]
            }
            """);

        return sb.ToString();
    }

    private AiSandboxResponse? Parse(string? rawResponse)
    {
        var json = AiJson.ExtractObject(rawResponse);
        if (json is null)
        {
            logger.LogWarning("AI sandbox response contained no JSON object");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiSandboxResponse>(json, AiJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "AI sandbox response was not valid JSON");
            return null;
        }
    }

    /// <summary>Shape of the JSON the agent is asked for; property names match the snake_case keys.</summary>
    private sealed class AiSandboxResponse
    {
        public string? Sandbox_setup_sql { get; set; }
        public string? Sandbox_teardown_sql { get; set; }
        public List<string>? Warnings { get; set; }
    }

    #endregion

    #region Helpers

    private const string Nl = "\n";

    private static string Key(string schema, string name) => $"{schema}.{name}".ToUpperInvariant();

    private static string Lit(string value) => CatalogRead.Literal(value);

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static List<string> Dedupe(IEnumerable<string> warnings) =>
        warnings.Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "(no schema context available)";
        return text.Length <= maxChars ? text : text[..maxChars] + "\n\n… (schema context truncated)";
    }

    [GeneratedRegex(@"^(?<lead>[ \t]*)GO(?<trail>[ \t]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex StandaloneGoPattern();

    #endregion
}
