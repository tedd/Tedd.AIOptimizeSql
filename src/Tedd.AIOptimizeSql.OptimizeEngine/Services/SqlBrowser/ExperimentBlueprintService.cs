using System.Diagnostics;
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
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Turns a query from the editor into a complete experiment proposal. Everything that can be
/// derived from catalog metadata is derived deterministically first; the AI is then handed that
/// scaffolding and asked only for the judgement calls. When the AI is unusable the deterministic
/// result stands on its own and a warning says so — the blueprint is never left half-filled.
/// </summary>
public sealed partial class ExperimentBlueprintService(
    ISchemaDiscoveryService schemaDiscovery,
    IObjectDependencyService dependencyService,
    AiAgentFactory agentFactory,
    ILogger<ExperimentBlueprintService> logger) : IExperimentBlueprintService
{
    /// <summary>Schema context is the bulk of the prompt; past this it is truncated with a note.</summary>
    private const int MaxSchemaContextChars = 60_000;

    private const int FingerprintCommandTimeoutSeconds = 600;
    private const int SandboxCommandTimeoutSeconds = 3600;

    #region Analyze

    /// <inheritdoc />
    public async Task<(ExperimentBlueprint Blueprint, string SchemaContextMarkdown, ObjectDependencyGraph Graph)>
        AnalyzeQueryAsync(string connectionString, string benchmarkSql, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkSql);

        logger.LogInformation("Analyzing benchmark SQL for a new experiment blueprint ({Length} chars)", benchmarkSql.Length);

        SchemaDiscoveryResult discovery;
        IReadOnlyList<SqlReferenceResolution> rootRefs;

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            rootRefs = await schemaDiscovery.ResolveSqlReferencesAsync(benchmarkSql, conn, ct);
            discovery = await schemaDiscovery.DiscoverSqlContextAsync(benchmarkSql, conn, ct);
        }

        var warnings = discovery.Warnings
            .Select(w => $"{w.ObjectName}: {w.Message}")
            .ToList();

        // The graph is a presentation aid: a failure there must not sink the whole wizard step.
        var graph = ObjectDependencyGraph.Empty;
        try
        {
            graph = await dependencyService.GetGraphForSqlAsync(connectionString, benchmarkSql, ct: ct);
            warnings.AddRange(graph.Warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dependency graph could not be built for the benchmark SQL");
            warnings.Add($"Dependency graph could not be built: {ex.Message}");
        }

        if (!OutputVerificationSqlBuilder.CanFingerprint(benchmarkSql, out var fingerprintLimitation))
        {
            warnings.Add(
                $"No output fingerprint could be generated automatically because {fingerprintLimitation}. " +
                "Supply an output verification script by hand, or run without output verification.");
        }

        var blueprint = new ExperimentBlueprint
        {
            Name = BuildDefaultName(discovery, rootRefs),
            BenchmarkSql = benchmarkSql,
            BaseTables = BuildBaseTables(discovery, rootRefs),
            OutputVerificationMode = OutputVerificationMode.UnorderedHash,
            OutputVerificationSql = OutputVerificationSqlBuilder.Build(benchmarkSql, OutputVerificationMode.UnorderedHash),
            Warnings = Dedupe(warnings)
        };

        logger.LogInformation(
            "Blueprint analysis complete: {Tables} base tables, {Nodes} dependency nodes, {Warnings} warnings",
            blueprint.BaseTables.Count, graph.Nodes.Count, blueprint.Warnings.Count);

        return (blueprint, discovery.MarkdownSummary, graph);
    }

    private static List<BlueprintTable> BuildBaseTables(
        SchemaDiscoveryResult discovery, IReadOnlyList<SqlReferenceResolution> rootRefs)
    {
        var direct = new HashSet<string>(
            rootRefs.Where(r => r.Resolved).Select(r => Key(r.Schema!, r.Name!)),
            StringComparer.OrdinalIgnoreCase);

        var kinds = discovery.Objects.ToDictionary(
            o => Key(o.Schema, o.Name), o => o.Kind, StringComparer.OrdinalIgnoreCase);

        var tables = new List<BlueprintTable>();
        foreach (var table in discovery.BaseTables
                     .OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Table, StringComparer.OrdinalIgnoreCase))
        {
            string reason;
            if (direct.Contains(Key(table.Schema, table.Table)))
            {
                reason = "read directly by the benchmark SQL";
            }
            else
            {
                // Triggers reference their own table, which would read as "via [dbo].[trg_x]"
                // for a table the query already touches directly. Modules only.
                var via = discovery.Dependencies
                    .Where(e => NameEquals(e.ReferencedSchema, table.Schema) && NameEquals(e.ReferencedName, table.Table))
                    .Where(e => !kinds.TryGetValue(Key(e.ReferencingSchema, e.ReferencingName), out var kind)
                                || kind != SqlObjectKind.Trigger)
                    .Select(e => SqlIdentifier.QuoteQualified(e.ReferencingSchema, e.ReferencingName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();

                reason = via.Count > 0
                    ? $"via {string.Join(", ", via)}"
                    : "reached through the benchmark's dependency graph";
            }

            tables.Add(new BlueprintTable
            {
                Schema = table.Schema,
                Table = table.Table,
                RowCount = table.RowCount,
                Include = true,
                Reason = reason
            });
        }

        return tables;
    }

    /// <summary>
    /// Names the experiment after the most interesting object the query names directly — a
    /// procedure beats a view beats a function beats a table.
    /// </summary>
    private static string BuildDefaultName(
        SchemaDiscoveryResult discovery, IReadOnlyList<SqlReferenceResolution> rootRefs)
    {
        var resolved = rootRefs.Where(r => r.Resolved && r.Schema != null && r.Name != null).ToList();
        if (resolved.Count == 0)
            return "Optimize ad-hoc query";

        var kinds = discovery.Objects.ToDictionary(
            o => Key(o.Schema, o.Name), o => o.Kind, StringComparer.OrdinalIgnoreCase);

        var primary = resolved
            .OrderBy(r => kinds.TryGetValue(Key(r.Schema!, r.Name!), out var kind) ? RankKind(kind) : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        return $"Optimize {SqlIdentifier.QuoteQualified(primary.Schema!, primary.Name!)}";
    }

    private static int RankKind(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.StoredProcedure => 0,
        SqlObjectKind.View => 1,
        SqlObjectKind.InlineTableValuedFunction => 2,
        SqlObjectKind.TableValuedFunction => 2,
        SqlObjectKind.ScalarFunction => 3,
        SqlObjectKind.Table => 4,
        _ => 5
    };

    #endregion

    #region Complete with AI

    /// <inheritdoc />
    public async Task<ExperimentBlueprint> CompleteWithAiAsync(
        AIConnection aiConnection,
        string connectionString,
        ExperimentBlueprintRequest request,
        ExperimentBlueprint current,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(current);

        progress?.Report("Building deterministic scaffolding…");

        var warnings = new List<string>(current.Warnings);

        var blueprint = new ExperimentBlueprint
        {
            Name = current.Name,
            Description = current.Description,
            Instructions = current.Instructions,
            BenchmarkSql = request.BenchmarkSql,
            ExperimentPreRunSql = current.ExperimentPreRunSql,
            ExperimentPostRunSql = current.ExperimentPostRunSql,
            HypothesisPreRunSql = current.HypothesisPreRunSql,
            HypothesisPostRunSql = current.HypothesisPostRunSql,
            IsolationMode = request.IsolationMode,
            OutputVerificationMode = request.OutputVerificationMode,
            MeasurementPlan = current.MeasurementPlan,
            BaseTables = (request.BaseTables.Count > 0 ? request.BaseTables : current.BaseTables)
                .Select(t => new BlueprintTable
                {
                    Schema = t.Schema,
                    Table = t.Table,
                    RowCount = t.RowCount,
                    Include = t.Include,
                    Reason = t.Reason
                })
                .ToList()
        };

        var databaseName = !string.IsNullOrWhiteSpace(request.DatabaseName)
            ? request.DatabaseName!
            : TryGetInitialCatalog(connectionString);

        var (sandboxSchemaName, sandboxDatabaseName, setupSql, teardownSql) =
            BuildSandboxScaffolding(request.IsolationMode, current, databaseName, blueprint.BaseTables, warnings);

        blueprint.SandboxSchemaName = sandboxSchemaName;
        blueprint.SandboxDatabaseName = sandboxDatabaseName;
        blueprint.SandboxSetupSql = setupSql;
        blueprint.SandboxTeardownSql = teardownSql;

        var deterministicVerificationSql =
            OutputVerificationSqlBuilder.Build(request.BenchmarkSql, request.OutputVerificationMode);

        string? fingerprintLimitation = null;
        if (request.OutputVerificationMode != OutputVerificationMode.None &&
            !OutputVerificationSqlBuilder.CanFingerprint(request.BenchmarkSql, out fingerprintLimitation))
        {
            warnings.Add(
                $"No output fingerprint could be generated automatically because {fingerprintLimitation}. " +
                "Review the output verification script before creating the experiment.");
        }

        blueprint.OutputVerificationSql = deterministicVerificationSql;

        AiBlueprintResponse? ai = null;
        string? aiError = null;

        try
        {
            progress?.Report($"Asking {aiConnection.Model} to complete the blueprint…");

            var agent = agentFactory.Create(aiConnection, AgentInstructions, new List<AITool>());
            var prompt = BuildPrompt(
                request, blueprint, databaseName, setupSql, teardownSql,
                deterministicVerificationSql, fingerprintLimitation);

            var sw = Stopwatch.StartNew();
            var response = await agent.RunAsync(prompt, cancellationToken: ct);
            sw.Stop();

            progress?.Report($"AI responded after {sw.ElapsedMilliseconds} ms, parsing…");

            ai = ParseResponse(response?.ToString());
            if (ai is null)
                aiError = "the response could not be parsed as JSON";
            else
                logger.LogInformation("AI completed the experiment blueprint in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            aiError = ex.Message;
            logger.LogError(ex, "AI could not complete the experiment blueprint");
        }

        if (aiError is not null)
        {
            warnings.Add(
                $"The AI could not be used to complete this blueprint ({aiError}). " +
                "Everything below is the deterministic scaffolding: names, instructions and the " +
                "sandbox scripts still need a human pass before the experiment is trustworthy.");
            progress?.Report("AI unavailable — keeping the deterministic blueprint.");
        }

        blueprint.Name = FirstNonBlank(ai?.Name, current.Name) ?? "Optimize query";
        blueprint.Description = FirstNonBlank(ai?.Description, current.Description);
        blueprint.Instructions = FirstNonBlank(ai?.Instructions, current.Instructions);
        blueprint.MeasurementPlan = FirstNonBlank(ai?.Measurement_plan, current.MeasurementPlan);
        blueprint.ExperimentPreRunSql = FirstNonBlank(ai?.Experiment_pre_run_sql, current.ExperimentPreRunSql);
        blueprint.ExperimentPostRunSql = FirstNonBlank(ai?.Experiment_post_run_sql, current.ExperimentPostRunSql);
        blueprint.HypothesisPreRunSql = FirstNonBlank(ai?.Hypothesis_pre_run_sql, current.HypothesisPreRunSql);
        blueprint.HypothesisPostRunSql = FirstNonBlank(ai?.Hypothesis_post_run_sql, current.HypothesisPostRunSql);

        if (request.IsolationMode != ExperimentIsolationMode.None)
        {
            blueprint.SandboxSetupSql = FirstNonBlank(ai?.Sandbox_setup_sql, setupSql);
            blueprint.SandboxTeardownSql = FirstNonBlank(ai?.Sandbox_teardown_sql, teardownSql);
        }

        // The deterministic fingerprint is preferred whenever it could be built at all: it is
        // known to cover every column. The AI's version is only taken when there is nothing to
        // prefer it over.
        if (request.OutputVerificationMode != OutputVerificationMode.None && fingerprintLimitation is not null)
        {
            var aiVerification = ai?.Output_verification_sql;
            if (!string.IsNullOrWhiteSpace(aiVerification) &&
                aiVerification.Contains("OutputHash", StringComparison.OrdinalIgnoreCase))
            {
                blueprint.OutputVerificationSql = aiVerification;
            }
        }

        if (ai?.Warnings is { Count: > 0 })
            warnings.AddRange(ai.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)));

        blueprint.Warnings = Dedupe(warnings);

        progress?.Report("Blueprint ready for review.");
        return blueprint;
    }

    /// <summary>
    /// Picks the sandbox names (keeping any the user already chose) and builds the deterministic
    /// setup/teardown pair for the isolation mode.
    /// </summary>
    private (string? SchemaName, string? DatabaseName, string? Setup, string? Teardown) BuildSandboxScaffolding(
        ExperimentIsolationMode mode,
        ExperimentBlueprint current,
        string databaseName,
        IReadOnlyList<BlueprintTable> tables,
        List<string> warnings)
    {
        switch (mode)
        {
            case ExperimentIsolationMode.SandboxSchema:
            {
                var schemaName = FirstNonBlank(current.SandboxSchemaName) ?? SandboxScriptBuilder.DefaultSandboxSchema;
                var (setup, teardown) = SandboxScriptBuilder.BuildSandboxSchema(schemaName, tables);
                return (schemaName, null, setup, teardown);
            }

            case ExperimentIsolationMode.CloneDatabase:
            {
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    warnings.Add(
                        "The target database name could not be determined from the connection string, so no " +
                        "clone-database scripts were generated. Set the sandbox database name and write the " +
                        "setup/teardown scripts before running.");
                    logger.LogWarning("Clone-database scaffolding skipped: no initial catalog in the connection string");
                    return (null, null, null, null);
                }

                var cloneName = FirstNonBlank(current.SandboxDatabaseName)
                                ?? SandboxScriptBuilder.DefaultCloneDatabaseName(databaseName);
                var (setup, teardown) = SandboxScriptBuilder.BuildCloneDatabase(cloneName, databaseName, tables);
                return (null, cloneName, setup, teardown);
            }

            default:
                return (null, null, null, null);
        }
    }

    #endregion

    #region Output verification

    /// <inheritdoc />
    public string? BuildOutputVerificationSql(string benchmarkSql, OutputVerificationMode mode) =>
        OutputVerificationSqlBuilder.Build(benchmarkSql, mode);

    /// <inheritdoc />
    public async Task<(bool Deterministic, string? FirstHash, string? SecondHash, string? Error)>
        TestOutputVerificationAsync(
            string connectionString, string verificationSql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(verificationSql))
            return (false, null, null, "There is no output verification script to test.");

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            var first = await ExecuteFingerprintAsync(conn, verificationSql, ct);
            var second = await ExecuteFingerprintAsync(conn, verificationSql, ct);

            if (first is null || second is null)
            {
                return (false, first, second,
                    "The script returned no value (NULL or no rows). It must return exactly one row " +
                    "with one column named OutputHash.");
            }

            if (!string.Equals(first, second, StringComparison.Ordinal))
            {
                logger.LogWarning("Output verification script is not stable: '{First}' then '{Second}'", first, second);
                return (false, first, second,
                    "The script returned two different fingerprints for the unmodified database, so it " +
                    "cannot prove an optimization safe. Usual causes: a non-deterministic expression " +
                    "(GETDATE, NEWID, ROW_NUMBER without a total order), unstable row order with " +
                    "OrderedHash, or concurrent writes to the tables it reads.");
            }

            return (true, first, second, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Output verification script failed to run");
            return (false, null, null, ex.Message);
        }
    }

    private static async Task<string?> ExecuteFingerprintAsync(
        SqlConnection connection, string script, CancellationToken ct)
    {
        object? last = null;
        foreach (var batch in MsSqlExecutor.SplitOnGo(script))
        {
            using var cmd = new SqlCommand(batch, connection) { CommandTimeout = FingerprintCommandTimeoutSeconds };
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is not null and not DBNull)
                last = value;
        }
        return last?.ToString();
    }

    #endregion

    #region Sandbox validation

    /// <inheritdoc />
    /// <remarks>
    /// This is the only method here that writes to the target database. Callers must not invoke
    /// it for a connection whose <c>AnalyzeOnly</c> flag is set — the interface has no place to
    /// pass the flag, so the check lives in the UI and the intent is logged loudly here.
    /// </remarks>
    public async Task<(bool Ok, string Log, string? Error)> ValidateSandboxAsync(
        string connectionString,
        ExperimentIsolationMode mode,
        string? setupSql,
        string? teardownSql,
        string? sandboxDatabaseName,
        CancellationToken ct = default)
    {
        if (mode == ExperimentIsolationMode.None)
            return (true, "Isolation mode None: nothing is provisioned, so there is nothing to validate.", null);

        logger.LogWarning(
            "Dry-running sandbox scripts for isolation mode {Mode} (sandbox database {Database}). " +
            "This CREATES and DROPS objects on the target server and must never run against an analyze-only connection.",
            mode, sandboxDatabaseName ?? "-");

        // CREATE/DROP DATABASE cannot run inside the database being created or dropped.
        var effectiveConnectionString = connectionString;
        if (mode == ExperimentIsolationMode.CloneDatabase)
        {
            effectiveConnectionString = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master"
            }.ConnectionString;
        }

        var log = new StringBuilder();
        log.AppendLine($"Validating isolation mode {mode}.");
        if (mode == ExperimentIsolationMode.CloneDatabase)
            log.AppendLine("Connected to [master] so CREATE/DROP DATABASE can run.");

        string? setupError = null;
        string? teardownError;

        try
        {
            if (string.IsNullOrWhiteSpace(setupSql))
            {
                setupError = "There is no sandbox setup script.";
                log.AppendLine("Setup: no script.");
            }
            else
            {
                setupError = await RunScriptAsync(effectiveConnectionString, setupSql, "setup", log, ct);
            }
        }
        finally
        {
            // Teardown runs even when setup threw or the caller cancelled: leaving a half-built
            // sandbox behind is worse than finishing the validation.
            if (string.IsNullOrWhiteSpace(teardownSql))
            {
                teardownError = "There is no sandbox teardown script, so anything setup created would be left behind.";
                log.AppendLine("Teardown: no script.");
            }
            else
            {
                teardownError = await RunScriptAsync(
                    effectiveConnectionString, teardownSql, "teardown", log, CancellationToken.None);
            }
        }

        string? error = (setupError, teardownError) switch
        {
            (null, null) => null,
            (not null, null) => setupError,
            (null, not null) => teardownError,
            _ => $"{setupError} {teardownError}"
        };

        return (error is null, log.ToString(), error);
    }

    /// <summary>
    /// Runs a script batch by batch, appending one log line per batch. Never throws: the failure
    /// is returned so the caller can still run teardown.
    /// </summary>
    private async Task<string?> RunScriptAsync(
        string connectionString, string script, string label, StringBuilder log, CancellationToken ct)
    {
        var batches = MsSqlExecutor.SplitOnGo(script);
        var batchNumber = 0;

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            foreach (var batch in batches)
            {
                batchNumber++;
                var sw = Stopwatch.StartNew();
                using var cmd = new SqlCommand(batch, conn) { CommandTimeout = SandboxCommandTimeoutSeconds };
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                sw.Stop();
                log.AppendLine($"[{label} {batchNumber}/{batches.Count}] ok in {sw.ElapsedMilliseconds} ms, {affected} row(s) affected.");
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sandbox {Label} script failed on batch {Batch}", label, batchNumber);
            log.AppendLine($"[{label} {batchNumber}/{batches.Count}] FAILED: {ex.Message}");
            return $"Sandbox {label} failed on batch {batchNumber}: {ex.Message}";
        }
    }

    #endregion

    #region AI prompt and response

    private const string AgentInstructions = """
        You are a SQL Server performance engineer preparing a controlled optimization experiment.

        An experiment measures one benchmark query, then lets an optimizing agent propose
        hypotheses (index changes, rewrites, statistics work) that are applied, benchmarked and
        reverted one at a time. Your job is to complete the experiment definition so that the
        measurement is fair, repeatable, and provably output-preserving.

        Rules, without exception:
        * T-SQL for Microsoft SQL Server only. No other dialect, no PowerShell, no shell.
        * Every identifier you emit is bracket-quoted and schema-qualified: [dbo].[Orders].
        * Scripts must be idempotent and safe to re-run. Guard with IF EXISTS / IF NOT EXISTS.
        * A teardown script must succeed even when its setup never ran.
        * Never invent syntax. If you are unsure a construct parses, leave the deterministic
          version untouched and explain the gap in "warnings".
        * Do not put anything destructive to the original data in any script. Sandbox scripts
          copy data; they never modify, delete or reorganize the source tables.
        """;

    private const string ResponseContract = """
        Reply with exactly one JSON object and nothing else: no prose before or after, no markdown
        code fence. Use null for a field that does not apply. Every SQL field is a single string
        that may contain newlines.

        {
          "name": "short experiment name, e.g. Optimize [dbo].[GetOrders]",
          "description": "what is slow, what will be tried, what success looks like",
          "instructions": "guidance and guard rails for the optimizing agent",
          "measurement_plan": "plain language: what is measured, how many runs, what makes a win",
          "experiment_pre_run_sql": "runs once before the experiment, or null",
          "experiment_post_run_sql": "runs once after the experiment, or null",
          "hypothesis_pre_run_sql": "runs before every hypothesis benchmark, or null",
          "hypothesis_post_run_sql": "runs after every hypothesis benchmark, or null",
          "sandbox_setup_sql": "complete sandbox setup, or null when isolation mode is None",
          "sandbox_teardown_sql": "complete sandbox teardown, or null when isolation mode is None",
          "output_verification_sql": "script returning one row, one column named OutputHash",
          "warnings": ["anything the user must check before running"]
        }
        """;

    private static string BuildPrompt(
        ExperimentBlueprintRequest request,
        ExperimentBlueprint blueprint,
        string databaseName,
        string? sandboxSetupSql,
        string? sandboxTeardownSql,
        string? outputVerificationSql,
        string? fingerprintLimitation)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Experiment to complete");
        sb.AppendLine();
        sb.AppendLine($"- Target database: {(string.IsNullOrWhiteSpace(databaseName) ? "(unknown)" : databaseName)}");
        sb.AppendLine($"- Isolation mode: {request.IsolationMode}");
        sb.AppendLine($"- Output verification mode: {request.OutputVerificationMode}");
        if (!string.IsNullOrWhiteSpace(blueprint.SandboxSchemaName))
            sb.AppendLine($"- Sandbox schema: {blueprint.SandboxSchemaName}");
        if (!string.IsNullOrWhiteSpace(blueprint.SandboxDatabaseName))
            sb.AppendLine($"- Sandbox database: {blueprint.SandboxDatabaseName}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Goal))
        {
            sb.AppendLine("## What the user wants");
            sb.AppendLine();
            sb.AppendLine(request.Goal);
            sb.AppendLine();
        }

        sb.AppendLine("## Benchmark SQL");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(request.BenchmarkSql);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Base tables the experiment depends on");
        sb.AppendLine();
        if (blueprint.BaseTables.Count == 0)
        {
            sb.AppendLine("(none were resolved from the catalog)");
        }
        else
        {
            sb.AppendLine("| Table | Rows | Included | Why |");
            sb.AppendLine("|-------|------|----------|-----|");
            foreach (var t in blueprint.BaseTables)
            {
                var rows = t.RowCount.HasValue ? t.RowCount.Value.ToString("N0") : "unknown";
                sb.AppendLine(
                    $"| `{SqlIdentifier.QuoteQualified(t.Schema, t.Table)}` | {rows} | " +
                    $"{(t.Include ? "yes" : "no")} | {t.Reason} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Schema context (deterministic catalog discovery)");
        sb.AppendLine();
        sb.AppendLine(Truncate(request.SchemaContextMarkdown, MaxSchemaContextChars));
        sb.AppendLine();

        if (request.IsolationMode == ExperimentIsolationMode.None)
        {
            sb.AppendLine("## Sandbox");
            sb.AppendLine();
            sb.AppendLine("Isolation mode is None: return null for sandbox_setup_sql and sandbox_teardown_sql.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Sandbox scaffolding to finish");
            sb.AppendLine();
            sb.AppendLine("The scripts below are generated and deliberately incomplete: `SELECT ... INTO` copies");
            sb.AppendLine("data and column types only. Return them completed — every `TODO(ai)` block replaced by");
            sb.AppendLine("real DDL that recreates the physical design (clustered and nonclustered indexes with");
            sb.AppendLine("INCLUDE lists and filters, primary/unique/foreign keys, DATA_COMPRESSION, columnstore,");
            sb.AppendLine("partitioning, identity/computed/default/check definitions, statistics), adapted to the");
            sb.AppendLine("row counts the copies actually get. Keep every IF EXISTS guard; teardown must still");
            sb.AppendLine("succeed when setup never ran.");
            sb.AppendLine();
            sb.AppendLine("### Generated setup");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(sandboxSetupSql ?? "(none generated)");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### Generated teardown");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(sandboxTeardownSql ?? "(none generated)");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Output verification");
        sb.AppendLine();
        if (request.OutputVerificationMode == OutputVerificationMode.None)
        {
            sb.AppendLine("Output verification is off: return null for output_verification_sql.");
        }
        else if (fingerprintLimitation is null)
        {
            sb.AppendLine("The generated script below already fingerprints every column of every row. Return it");
            sb.AppendLine("unchanged in output_verification_sql unless it cannot work for this query; if you change");
            sb.AppendLine("it, it must still return exactly one row with one column named OutputHash.");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(outputVerificationSql ?? "(none generated)");
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine($"No script could be generated automatically because {fingerprintLimitation}.");
            sb.AppendLine("Write one: materialise the benchmark's rows, hash every column of every row, combine the");
            sb.AppendLine($"row hashes ({(request.OutputVerificationMode == OutputVerificationMode.OrderedHash ? "order-sensitively" : "order-insensitively")}),");
            sb.AppendLine("and return exactly one row with one column named OutputHash.");
        }
        sb.AppendLine();

        sb.AppendLine("## Response format");
        sb.AppendLine();
        sb.AppendLine(ResponseContract);

        return sb.ToString();
    }

    /// <summary>
    /// Tolerant JSON parse: models wrap objects in prose or code fences even when told not to,
    /// so strip fences and take the outermost balanced object before deserializing.
    /// </summary>
    private AiBlueprintResponse? ParseResponse(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;

        var text = rawResponse.Trim();
        var fence = CodeFencePattern().Match(text);
        if (fence.Success)
            text = fence.Groups[1].Value.Trim();

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            logger.LogWarning("AI blueprint response contained no JSON object");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiBlueprintResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "AI blueprint response was not valid JSON");
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;

            if (depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Shape of the JSON the agent is asked for; property names match the snake_case keys.</summary>
    private sealed class AiBlueprintResponse
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public string? Measurement_plan { get; set; }
        public string? Experiment_pre_run_sql { get; set; }
        public string? Experiment_post_run_sql { get; set; }
        public string? Hypothesis_pre_run_sql { get; set; }
        public string? Hypothesis_post_run_sql { get; set; }
        public string? Sandbox_setup_sql { get; set; }
        public string? Sandbox_teardown_sql { get; set; }
        public string? Output_verification_sql { get; set; }
        public List<string>? Warnings { get; set; }
    }

    [GeneratedRegex(@"^```(?:json|sql)?\s*\n([\s\S]*?)\n\s*```\s*$", RegexOptions.Compiled)]
    private static partial Regex CodeFencePattern();

    #endregion

    #region Helpers

    private static string Key(string schema, string name) => $"{schema}.{name}".ToUpperInvariant();

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static List<string> Dedupe(IEnumerable<string> warnings) =>
        warnings
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "(no schema context available)";
        return text.Length <= maxChars ? text : text[..maxChars] + "\n\n… (schema context truncated)";
    }

    /// <summary>Reads the initial catalog from a connection string; empty when it has none or is malformed.</summary>
    private string TryGetInitialCatalog(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the initial catalog from the connection string");
            return "";
        }
    }

    #endregion
}
