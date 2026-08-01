using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
public sealed class ExperimentBlueprintService(
    ISchemaDiscoveryService schemaDiscovery,
    IObjectDependencyService dependencyService,
    ISandboxScriptService sandboxScriptService,
    AiAgentFactory agentFactory,
    AiConversationTracker conversationTracker,
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

        var baseTables = BuildBaseTables(discovery, rootRefs);
        var affectedTables = baseTables.Select(t => (t.Schema, t.Table)).ToList();

        if (!OutputVerificationSqlBuilder.CanFingerprint(benchmarkSql, out var fingerprintLimitation))
        {
            warnings.Add(affectedTables.Count > 0
                ? $"The benchmark's own result set could not be fingerprinted because {fingerprintLimitation}. " +
                  $"Falling back to a table-state fingerprint over the {affectedTables.Count} table(s) it touches -- " +
                  "review it before relying on it, especially if the benchmark writes through tables the dependency graph could not follow."
                : $"No output fingerprint could be generated automatically because {fingerprintLimitation}, " +
                  "and no affected tables were discovered to fall back to. " +
                  "Supply an output verification script by hand, or run without output verification.");
        }

        var blueprint = new ExperimentBlueprint
        {
            Name = BuildDefaultName(discovery, rootRefs),
            BenchmarkSql = benchmarkSql,
            BaseTables = baseTables,
            OutputVerificationMode = OutputVerificationMode.UnorderedHash,
            OutputVerificationSql = OutputVerificationSqlBuilder.Build(
                benchmarkSql, OutputVerificationMode.UnorderedHash, affectedTables),
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
            await BuildSandboxScaffoldingAsync(
                connectionString, request, current, databaseName, blueprint.BaseTables, warnings, ct);

        blueprint.SandboxSchemaName = sandboxSchemaName;
        blueprint.SandboxDatabaseName = sandboxDatabaseName;
        blueprint.SandboxSetupSql = setupSql;
        blueprint.SandboxTeardownSql = teardownSql;

        var affectedIncludedTables = request.BaseTables.Where(t => t.Include).Select(t => (t.Schema, t.Table)).ToList();
        var deterministicVerificationSql = OutputVerificationSqlBuilder.Build(
            request.BenchmarkSql, request.OutputVerificationMode, affectedIncludedTables);

        string? fingerprintLimitation = null;
        var usingTableStateFallback = false;
        if (request.OutputVerificationMode != OutputVerificationMode.None &&
            !OutputVerificationSqlBuilder.CanFingerprint(request.BenchmarkSql, out fingerprintLimitation))
        {
            usingTableStateFallback = affectedIncludedTables.Count > 0;
            warnings.Add(usingTableStateFallback
                ? $"The benchmark's own result set could not be fingerprinted because {fingerprintLimitation}. " +
                  $"Falling back to a table-state fingerprint over the {affectedIncludedTables.Count} affected table(s) -- " +
                  "review it before relying on it."
                : $"No output fingerprint could be generated automatically because {fingerprintLimitation}, " +
                  "and no affected tables were available to fall back to. Review the output verification script before creating the experiment.");
        }

        blueprint.OutputVerificationSql = deterministicVerificationSql;

        AiBlueprintResponse? ai = null;
        string? aiError = null;

        var conversation = await conversationTracker.StartAsync(new AiConversationStart
        {
            Kind = AiConversationKind.ExperimentBlueprint,
            AiConnection = aiConnection,
            DatabaseConnectionId = request.DatabaseConnectionId,
            Title = $"Experiment wizard: {FirstNonBlank(current.Name, "new experiment")}",
        }, ct);

        try
        {
            progress?.Report($"Asking {aiConnection.Model} to complete the blueprint…");

            var agent = agentFactory.Create(aiConnection, AgentInstructions, new List<AITool>());
            var prompt = BuildPrompt(
                request, blueprint, databaseName, setupSql, teardownSql,
                deterministicVerificationSql, fingerprintLimitation, usingTableStateFallback);

            var sw = Stopwatch.StartNew();
            var response = await agent.RunAsync(prompt, cancellationToken: ct);
            sw.Stop();
            conversation.Record(response?.Usage);
            await conversation.CompleteAsync(CancellationToken.None);

            progress?.Report($"AI responded after {sw.ElapsedMilliseconds} ms, parsing…");

            ai = ParseResponse(response?.ToString());
            if (ai is null)
                aiError = "the response could not be parsed as JSON";
            else
                logger.LogInformation("AI completed the experiment blueprint in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            await conversation.FailAsync("Cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await conversation.FailAsync(ex.Message, CancellationToken.None);
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
    /// Picks the sandbox names (keeping any the user already chose) and generates the
    /// setup/teardown pair from the live catalog, so the AI is handed working scripts to review
    /// rather than scaffolding to fill in. Only when the catalog cannot be read does it fall back
    /// to <see cref="SandboxScriptBuilder"/>'s offline outline.
    /// </summary>
    private async Task<(string? SchemaName, string? DatabaseName, string? Setup, string? Teardown)>
        BuildSandboxScaffoldingAsync(
            string connectionString,
            ExperimentBlueprintRequest request,
            ExperimentBlueprint current,
            string databaseName,
            IReadOnlyList<BlueprintTable> tables,
            List<string> warnings,
            CancellationToken ct)
    {
        string? schemaName = null;
        string? cloneName = null;

        switch (request.IsolationMode)
        {
            case ExperimentIsolationMode.SandboxSchema:
                schemaName = FirstNonBlank(current.SandboxSchemaName) ?? SandboxScriptBuilder.DefaultSandboxSchema;
                break;

            case ExperimentIsolationMode.CloneDatabase:
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    warnings.Add(
                        "The target database name could not be determined from the connection string, so no " +
                        "clone-database scripts were generated. Set the sandbox database name and write the " +
                        "setup/teardown scripts before running.");
                    logger.LogWarning("Clone-database scaffolding skipped: no initial catalog in the connection string");
                    return (null, null, null, null);
                }

                cloneName = FirstNonBlank(current.SandboxDatabaseName)
                            ?? SandboxScriptBuilder.DefaultCloneDatabaseName(databaseName);
                break;

            default:
                return (null, null, null, null);
        }

        try
        {
            var scripts = await sandboxScriptService.GenerateAsync(connectionString, new SandboxScriptRequest
            {
                IsolationMode = request.IsolationMode,
                DatabaseConnectionId = request.DatabaseConnectionId,
                SandboxSchemaName = schemaName,
                SandboxDatabaseName = cloneName,
                BenchmarkSql = request.BenchmarkSql,
                Tables = tables
            }, ct);

            warnings.AddRange(scripts.Warnings);
            if (!string.IsNullOrWhiteSpace(scripts.Setup))
                return (schemaName, cloneName, scripts.Setup, scripts.Teardown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Catalog-driven sandbox generation failed; falling back to the offline outline");
            warnings.Add(
                $"The sandbox scripts could not be generated from the catalog ({ex.Message}). What follows is an " +
                "outline the AI is asked to finish, not a working script — review it before running.");
        }

        var fallback = request.IsolationMode == ExperimentIsolationMode.SandboxSchema
            ? SandboxScriptBuilder.BuildSandboxSchema(schemaName!, tables)
            : SandboxScriptBuilder.BuildCloneDatabase(cloneName!, databaseName, tables);

        return (schemaName, cloneName, fallback.Setup, fallback.Teardown);
    }

    #endregion

    #region Output verification

    /// <inheritdoc />
    public string? BuildOutputVerificationSql(
        string benchmarkSql, OutputVerificationMode mode, IReadOnlyList<(string Schema, string Table)>? affectedTables = null) =>
        OutputVerificationSqlBuilder.Build(benchmarkSql, mode, affectedTables);

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
        string? fingerprintLimitation,
        bool usingTableStateFallback)
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
        else if (usingTableStateFallback)
        {
            sb.AppendLine($"The benchmark is not a single SELECT ({fingerprintLimitation}), most likely because it is a");
            sb.AppendLine("stored procedure with side effects and no result set of its own -- there is nothing to hash");
            sb.AppendLine("by row. The script below fingerprints TABLE STATE instead: it hashes the current contents");
            sb.AppendLine("of every table in \"Base tables the experiment depends on\" above and combines them into one");
            sb.AppendLine("value. Run once before the benchmark and once after each hypothesis, a match proves the");
            sb.AppendLine("optimized procedure left the same data behind as the original -- the only meaningful \"same");
            sb.AppendLine("output\" a procedure with no return rows can have. Review it:");
            sb.AppendLine("  - If the procedure writes through tables NOT listed above (dynamic SQL, a nested call the");
            sb.AppendLine("    dependency graph could not follow), rewrite the script to include them, or say so in warnings.");
            sb.AppendLine("  - If a hypothesis benchmark would run the procedure MORE THAN ONCE (warm-up + timed");
            sb.AppendLine("    iterations) and the procedure is not naturally idempotent -- e.g. it INSERTs new rows");
            sb.AppendLine("    every call rather than upserting -- repeated runs will pollute the tables between");
            sb.AppendLine("    iterations and the fingerprint will never repeat. When that risk exists and the isolation");
            sb.AppendLine("    mode is SandboxSchema or CloneDatabase, use the sandbox to neutralise it: propose (in the");
            sb.AppendLine("    sandbox setup script) a shadow variant of the procedure -- and, if it is cleaner, a whole");
            sb.AppendLine("    parallel set of procedures and staging tables -- that redirects every write to sandboxed");
            sb.AppendLine("    copies, optionally truncating/resetting those staging tables between runs, so the same");
            sb.AppendLine("    benchmark can execute repeatedly without accumulating state or ever touching the real");
            sb.AppendLine("    tables. Explain that redirection in warnings so the user can review the substitute");
            sb.AppendLine("    procedure(s) before trusting them. When isolation mode is None, this risk cannot be");
            sb.AppendLine("    engineered away -- say so plainly in warnings instead of inventing a workaround.");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(outputVerificationSql ?? "(none generated)");
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine($"No script could be generated automatically because {fingerprintLimitation}, and no base");
            sb.AppendLine("tables were resolved to fall back to (see \"Base tables the experiment depends on\" above --");
            sb.AppendLine("it is empty). Two ways to fix this, in order of preference:");
            sb.AppendLine("  1. If the benchmark writes to tables the dependency graph could not resolve (dynamic SQL,");
            sb.AppendLine("     a linked server, a synonym it could not follow), name those tables explicitly and write");
            sb.AppendLine("     a table-state fingerprint over them: hash each table's rows with");
            sb.AppendLine("     HASHBYTES('SHA2_256', (SELECT [t].* FOR XML RAW, BINARY BASE64)) per row, combine the");
            sb.AppendLine("     row hashes commutatively (SUM/CHECKSUM_AGG), then combine the per-table results.");
            sb.AppendLine("  2. If the benchmark genuinely returns a result set that just could not be wrapped");
            sb.AppendLine("     mechanically, materialise its rows, hash every column of every row, combine the row");
            sb.AppendLine($"     hashes ({(request.OutputVerificationMode == OutputVerificationMode.OrderedHash ? "order-sensitively" : "order-insensitively")}).");
            sb.AppendLine("Either way the script must return exactly one row with one column named OutputHash. If");
            sb.AppendLine("neither applies, return null and explain why in warnings -- do not guess at a fingerprint");
            sb.AppendLine("you cannot justify.");
        }
        sb.AppendLine();

        sb.AppendLine("## Response format");
        sb.AppendLine();
        sb.AppendLine(ResponseContract);

        return sb.ToString();
    }

    private AiBlueprintResponse? ParseResponse(string? rawResponse)
    {
        var json = AiJson.ExtractObject(rawResponse);
        if (json is null)
        {
            logger.LogWarning("AI blueprint response contained no JSON object");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiBlueprintResponse>(json, AiJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "AI blueprint response was not valid JSON");
            return null;
        }
    }

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

    #endregion

    #region Draft from an analysis finding

    /// <inheritdoc />
    public async Task<FindingExperimentDraft> DraftFromFindingAsync(
        AIConnection aiConnection,
        FindingExperimentContext finding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aiConnection);
        ArgumentNullException.ThrowIfNull(finding);

        var conversation = await conversationTracker.StartAsync(new AiConversationStart
        {
            Kind = AiConversationKind.ExperimentBlueprint,
            AiConnection = aiConnection,
            DatabaseConnectionId = finding.DatabaseConnectionId,
            Title = $"Draft experiment for finding: {finding.Title}",
        }, ct);

        try
        {
            var agent = agentFactory.Create(aiConnection, FindingDraftInstructions, new List<AITool>());
            var response = await agent.RunAsync(BuildFindingPrompt(finding), cancellationToken: ct);
            conversation.Record(response?.Usage);
            await conversation.CompleteAsync(CancellationToken.None);

            var json = AiJson.ExtractObject(response?.ToString());
            if (json is null)
                return new FindingExperimentDraft(null, null, null, null,
                    "The AI replied with something that is not the expected JSON.");

            var draft = JsonSerializer.Deserialize<AiFindingDraftResponse>(json, AiJson.Options);
            if (draft is null || string.IsNullOrWhiteSpace(draft.Benchmark_sql))
                return new FindingExperimentDraft(draft?.Name, null, draft?.Goal, draft?.Instructions,
                    "The AI could not turn this finding into a benchmark query. Write the query you want measured.");

            return new FindingExperimentDraft(
                draft.Name, draft.Benchmark_sql, draft.Goal, draft.Instructions, Error: null);
        }
        catch (OperationCanceledException)
        {
            await conversation.FailAsync("Cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await conversation.FailAsync(ex.Message, CancellationToken.None);
            logger.LogError(ex, "The AI could not draft an experiment from an analysis finding");
            return new FindingExperimentDraft(null, null, null, null,
                $"The AI could not be reached: {ex.Message}");
        }
    }

    private const string FindingDraftInstructions = """
        You turn a database analysis finding into an experiment that will prove or disprove it.

        An experiment measures ONE query before and after a change. Your job is to write the query
        whose timing would actually move if the finding were addressed — not a demonstration of the
        problem, and not the fix itself.

        Rules, without exception:
        * T-SQL for Microsoft SQL Server only. Every identifier bracket-quoted and schema-qualified.
        * The benchmark query is READ-ONLY and safe to run repeatedly: no INSERT/UPDATE/DELETE/DDL.
        * It must be runnable as-is. Where the finding does not tell you the real parameter values,
          declare variables with plausible literals at the top rather than leaving placeholders.
        * If the finding names an object, the query must actually touch that object.
        * Prefer a realistic workload over a synthetic one: a query a real caller would issue.

        Reply with JSON only, no prose and no markdown fences:
        {"name": "...", "benchmark_sql": "...", "goal": "...", "instructions": "..."}

        name         short experiment name
        benchmark_sql the query to measure
        goal         one sentence on what "faster" means here, in the user's terms
        instructions guard rails for the optimizing AI, e.g. which objects it must not change
        """;

    private static string BuildFindingPrompt(FindingExperimentContext finding)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Analysis finding");
        sb.AppendLine($"Title: {finding.Title}");
        if (!string.IsNullOrWhiteSpace(finding.Severity))
            sb.AppendLine($"Severity: {finding.Severity}");
        if (!string.IsNullOrWhiteSpace(finding.Category))
            sb.AppendLine($"Category: {finding.Category}");
        if (!string.IsNullOrWhiteSpace(finding.ObjectName))
            sb.AppendLine($"Affected object: [{finding.ObjectSchema ?? "dbo"}].[{finding.ObjectName}]");
        if (!string.IsNullOrWhiteSpace(finding.DatabaseName))
            sb.AppendLine($"Database: {finding.DatabaseName}");

        if (!string.IsNullOrWhiteSpace(finding.Description))
        {
            sb.AppendLine();
            sb.AppendLine("## What was found");
            sb.AppendLine(finding.Description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(finding.Evidence))
        {
            sb.AppendLine();
            sb.AppendLine("## Evidence");
            var evidence = finding.Evidence.Trim();
            sb.AppendLine(evidence.Length <= MaxSchemaContextChars
                ? evidence
                : evidence[..MaxSchemaContextChars] + "\n\n… (evidence truncated)");
        }

        if (!string.IsNullOrWhiteSpace(finding.Recommendation))
        {
            sb.AppendLine();
            sb.AppendLine("## Recommendation");
            sb.AppendLine(finding.Recommendation.Trim());
        }

        if (!string.IsNullOrWhiteSpace(finding.RecommendationSql))
        {
            sb.AppendLine();
            sb.AppendLine("## Suggested remediation SQL (this is the FIX, not the benchmark)");
            sb.AppendLine("```sql");
            sb.AppendLine(finding.RecommendationSql.Trim());
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("Write the benchmark query that would demonstrate the improvement if this finding were addressed, and return it as JSON.");
        return sb.ToString();
    }

    /// <summary>Shape of the JSON the finding-draft agent is asked for.</summary>
    private sealed class AiFindingDraftResponse
    {
        public string? Name { get; set; }
        public string? Benchmark_sql { get; set; }
        public string? Goal { get; set; }
        public string? Instructions { get; set; }
    }

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
