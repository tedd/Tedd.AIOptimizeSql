using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

public sealed class AiHypothesisService(
    AiAgentFactory agentFactory,
    ISchemaDiscoveryService schemaDiscoveryService,
    HypothesisTestingService hypothesisTestingService,
    AgentTaskLoopRunner taskLoopRunner,
    IServiceScopeFactory scopeFactory,
    IOptions<OptimizeEngineSettings> settings,
    ILoggerFactory loggerFactory) : IAiHypothesisService
{
    private const int MaxLogMessageChars = 48_000;

    private readonly ILogger _logger = loggerFactory.CreateLogger<AiHypothesisService>();

    public async Task RunIterationAsync(
        ResearchIterationId iterationId,
        CancellationToken cancellationToken = default,
        string? runStartedLogLine = null)
    {
        _logger.LogInformation("Starting hypothesis generation loop for research iteration {IterationId}", iterationId);

        var iteration = await LoadIterationAsync(iterationId, cancellationToken);
        if (iteration is null)
        {
            _logger.LogWarning("Research iteration {IterationId} not found, aborting", iterationId);
            return;
        }

        // Run deterministic schema discovery if not already done
        if (string.IsNullOrWhiteSpace(iteration.SchemaDiscoveryMarkdown))
            await RunSchemaDiscoveryAsync(iteration, cancellationToken);

        var analyzeOnly = iteration.Experiment?.DatabaseConnection?.AnalyzeOnly == true;

        // Run baseline benchmark if not already done. In analyze-only mode we never
        // benchmark: it clears caches and updates statistics, which mutates server state.
        BenchmarkRun? baseline = null;
        if (analyzeOnly)
        {
            _logger.LogInformation(
                "Research iteration {IterationId} targets an analyze-only connection; baseline benchmark and hypothesis testing are skipped",
                iterationId);
        }
        else if (iteration.BaselineBenchmarkRunId == null && !string.IsNullOrWhiteSpace(iteration.Experiment?.BenchmarkSql))
        {
            await UpdateIterationMessageAsync(iterationId, "Running baseline benchmark...", cancellationToken);
            baseline = await hypothesisTestingService.RunBaselineBenchmarkAsync(iteration, cancellationToken);

            // Reload iteration to pick up the baseline link
            iteration = await LoadIterationAsync(iterationId, cancellationToken)
                ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared.");
        }
        else if (iteration.BaselineBenchmarkRunId != null)
        {
            baseline = await LoadBenchmarkRunAsync(iteration.BaselineBenchmarkRunId.Value, cancellationToken);
        }

        var pendingRunStartedLog = runStartedLogLine;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gate = await TryGetIterationRunGateAsync(iterationId, cancellationToken);
            if (gate is null)
            {
                _logger.LogWarning("Research iteration {IterationId} not found during hypothesis loop", iterationId);
                return;
            }

            var hypothesisCount = await CountHypothesesForIterationAsync(iterationId, cancellationToken);
            var maxHypotheses = Math.Max(1, gate.MaxNumberOfHypotheses);

            if (hypothesisCount >= maxHypotheses)
            {
                _logger.LogInformation(
                    "Research iteration {IterationId} reached hypothesis cap ({Current}/{Max}), finishing main loop",
                    iterationId, hypothesisCount, maxHypotheses);
                break;
            }

            if (gate.State is ResearchIterationState.Stopped or ResearchIterationState.Paused)
            {
                _logger.LogInformation("Research iteration {IterationId} is {State}, stopping generation loop", iterationId, gate.State);
                await UpdateIterationMessageAsync(iterationId,
                    gate.State == ResearchIterationState.Paused ? "Paused" : "Stopped by user",
                    cancellationToken);
                return;
            }

            await UpdateIterationMessageAsync(iterationId,
                $"Generating hypothesis {hypothesisCount + 1} of {maxHypotheses}",
                cancellationToken);

            iteration = await LoadIterationAsync(iterationId, cancellationToken)
                ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared during processing.");

            var priorHypotheses = await GetPriorHypothesesAsync(iterationId, cancellationToken);

            var bestPrior = priorHypotheses
                .Where(h => h.Status == HypothesisState.Completed && h.ImpovementPercentage > 0)
                .OrderByDescending(h => h.ImpovementPercentage)
                .FirstOrDefault();

            var placeholder = await InsertPendingHypothesisAsync(iterationId, hypothesisCount + 1, bestPrior?.Id, cancellationToken);

            if (pendingRunStartedLog is not null)
            {
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    pendingRunStartedLog,
                    "QueueMonitor",
                    cancellationToken);
                pendingRunStartedLog = null;
            }

            await AppendHypothesisLogAsync(
                placeholder.Id,
                $"Hypothesis record created (pending). Target slot {hypothesisCount + 1} of {maxHypotheses}.",
                "HypothesisService",
                cancellationToken);

            try
            {
                await UpdateHypothesisStatusAsync(placeholder.Id, HypothesisState.Generating, cancellationToken);
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    "Status set to Generating; preparing AI agent and database tools.",
                    "HypothesisService",
                    cancellationToken);

                var result = await GenerateSingleHypothesisAsync(iteration, priorHypotheses, placeholder.Id, baseline, cancellationToken);

                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    $"AI agent finished in {result.TimeUsedMs} ms. Description length: {result.Description?.Length ?? 0}, OptimizeSql: {(result.OptimizeSql != null ? $"{result.OptimizeSql.Length} chars" : "none")}, RevertSql: {(result.RevertSql != null ? $"{result.RevertSql.Length} chars" : "none")}.",
                    "HypothesisService",
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(result.Description))
                {
                    await AppendHypothesisLogAsync(
                        placeholder.Id,
                        $"AI output (preview):\n{TruncateForLog(result.Description!, maxChars: 6_000)}",
                        "HypothesisService",
                        cancellationToken);
                }

                await FinalizeHypothesisAsync(placeholder.Id, result.Description,
                    result.OptimizeSql, result.RevertSql, result.TimeUsedMs, cancellationToken);
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    "Hypothesis finalized (Generated).",
                    "HypothesisService",
                    cancellationToken);

                if (analyzeOnly && !string.IsNullOrWhiteSpace(result.OptimizeSql))
                {
                    await AppendHypothesisLogAsync(
                        placeholder.Id,
                        "Analyze-only connection: hypothesis stored for review only. Apply/Benchmark/Revert is skipped — the target database is never modified.",
                        "HypothesisService",
                        cancellationToken);
                }

                // Test hypothesis if it has executable SQL and we have a baseline
                if (!analyzeOnly && !string.IsNullOrWhiteSpace(result.OptimizeSql) && baseline != null)
                {
                    await AppendHypothesisLogAsync(
                        placeholder.Id,
                        HypothesisTestingService.WithSql("Starting Apply → Benchmark → Revert cycle.", result.OptimizeSql),
                        "HypothesisService",
                        cancellationToken);

                    // Reload iteration to get fresh state
                    iteration = await LoadIterationAsync(iterationId, cancellationToken)
                        ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared.");

                    var revertOk = await hypothesisTestingService.TestHypothesisAsync(
                        placeholder.Id, iteration, baseline,
                        (hid, msg, src) => AppendHypothesisLogAsync(hid, msg, src, CancellationToken.None).GetAwaiter().GetResult(),
                        cancellationToken);

                    if (!revertOk)
                    {
                        _logger.LogError("Revert failed for hypothesis {Id}, halting iteration {IterationId}", placeholder.Id, iterationId);
                        await UpdateIterationMessageAsync(iterationId,
                            "HALTED: Revert failed - database may be in modified state", CancellationToken.None);
                        return;
                    }
                }

                _logger.LogInformation(
                    "Hypothesis #{Number} finished for research iteration {IterationId}",
                    hypothesisCount + 1, iterationId);
            }
            catch (OperationCanceledException)
            {
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    "Generation cancelled (operation aborted).",
                    "HypothesisService",
                    CancellationToken.None);
                await FailHypothesisAsync(placeholder.Id, "Cancelled", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate hypothesis #{Number} for research iteration {IterationId}",
                    hypothesisCount + 1, iterationId);
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    $"Generation failed:\n{TruncateForLog(ex.ToString())}",
                    "HypothesisService",
                    CancellationToken.None);
                await FailHypothesisAsync(placeholder.Id, ex.Message, CancellationToken.None);
            }

            var gateAfter = await TryGetIterationRunGateAsync(iterationId, CancellationToken.None);
            if (gateAfter is null)
            {
                _logger.LogWarning("Research iteration {IterationId} disappeared after hypothesis", iterationId);
                return;
            }

            if (gateAfter.State is ResearchIterationState.Stopped or ResearchIterationState.Paused)
            {
                _logger.LogInformation("Research iteration {IterationId} is {State} after hypothesis, stopping", iterationId, gateAfter.State);
                await UpdateIterationMessageAsync(iterationId,
                    gateAfter.State == ResearchIterationState.Paused ? "Paused" : "Stopped by user",
                    CancellationToken.None);
                return;
            }
        }

        iteration = await LoadIterationAsync(iterationId, cancellationToken)
            ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared.");

        var countAfterMainLoop = await CountHypothesesForIterationAsync(iterationId, cancellationToken);
        await UpdateIterationMessageAsync(iterationId,
            $"All {countAfterMainLoop} hypotheses generated, checking for combined optimization...",
            cancellationToken);

        _logger.LogInformation("Research iteration {IterationId} hypothesis loop completed ({Count} hypotheses)", iterationId, countAfterMainLoop);

        // Combined optimization: if 2+ hypotheses succeeded, ask AI to combine the best
        await RunCombinedOptimizationAsync(iterationId, iteration, baseline, cancellationToken);

        var totalHypotheses = await CountHypothesesForIterationAsync(iterationId, cancellationToken);
        await UpdateIterationMessageAsync(iterationId,
            $"All {totalHypotheses} hypotheses generated and tested",
            cancellationToken);
    }

    public async Task AppendLogToLatestHypothesisInIterationAsync(
        ResearchIterationId iterationId,
        string message,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        if (!await db.Hypotheses.AnyAsync(h => h.ResearchIterationId == iterationId, cancellationToken))
            return;

        var latestId = await db.Hypotheses.AsNoTracking()
            .Where(h => h.ResearchIterationId == iterationId)
            .OrderByDescending(h => h.Id)
            .Select(h => h.Id)
            .FirstAsync(cancellationToken);

        await AppendHypothesisLogAsync(latestId, message, source, cancellationToken);
    }

    /// <summary>Result of a single AI hypothesis generation call.</summary>
    internal sealed record HypothesisGenerationResult(
        string? Description, string? OptimizeSql, string? RevertSql, long TimeUsedMs);

    private async Task<HypothesisGenerationResult> GenerateSingleHypothesisAsync(
        ResearchIteration iteration,
        IReadOnlyList<Hypothesis> priorHypotheses,
        HypothesisId hypothesisId,
        BenchmarkRun? baseline,
        CancellationToken cancellationToken)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded on the research iteration.");
        var aiConnection = iteration.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded on the research iteration.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded on the experiment.");

        var analyzeOnly = dbConnection.AnalyzeOnly;

        var executor = DatabaseExecutorFactory.Create(
            new BenchmarkConfig { DatabaseType = "MSSQL" },
            msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, cancellationToken);
        using var toolWrapper = new SqlToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<SqlToolWrapper>(), readOnly: analyzeOnly);
        using var schemaTools = new SchemaInspectionToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<SchemaInspectionToolWrapper>());
        using var perfTools = new PerformanceMetricsToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<PerformanceMetricsToolWrapper>());
        using var webTools = CreateWebSearchTools();
        var taskTools = new AgentTaskToolWrapper(
            AgentTaskScope.ForHypothesis(hypothesisId),
            scopeFactory, loggerFactory.CreateLogger<AgentTaskToolWrapper>());
        var benchmarkTools = new BenchmarkRunToolWrapper(
            iteration.Id, scopeFactory, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<BenchmarkRunToolWrapper>());

        var tools = BuildAgentTools(toolWrapper, schemaTools, perfTools, webTools, taskTools, benchmarkTools, analyzeOnly);

        var relatedFindings = await GetRelatedFindingsAsync(experiment.Id, cancellationToken);

        var maxRuns = Math.Clamp(settings.Value.MaxAgentContinuations, 1, 100);
        var instructions = HypothesisPromptBuilder.BuildInstructions(
            experiment, iteration, priorHypotheses,
            schemaDiscoveryMarkdown: iteration.SchemaDiscoveryMarkdown,
            baselinePerformanceSummary: baseline is null ? null : HypothesisPromptBuilder.FormatBenchmarkRunSummary(baseline),
            analyzeOnly: analyzeOnly,
            maxAgentRuns: maxRuns,
            relatedFindings: relatedFindings);

        var agent = agentFactory.Create(aiConnection, instructions, tools);

        var prompt = HypothesisPromptBuilder.BuildPrompt(iteration, priorHypotheses);

        _logger.LogInformation("Invoking AI agent for research iteration {IterationId}, hypothesis #{Number}",
            iteration.Id, priorHypotheses.Count + 1);

        await AppendHypothesisLogAsync(
            hypothesisId,
            $"Invoking AI agent (model context from iteration). Prior hypotheses in iteration: {priorHypotheses.Count}. " +
            $"Related analysis findings included in prompt: {relatedFindings.Count}. Task-loop limit: {maxRuns} runs.",
            "HypothesisService",
            cancellationToken);

        var loop = await taskLoopRunner.RunAsync(
            agent,
            prompt,
            AgentTaskScope.ForHypothesis(hypothesisId),
            isResponseAcceptable: r => AiResponseParser.ParseHypothesisResponse(r) != null,
            shouldAbort: async abortCt =>
            {
                var gate = await TryGetIterationRunGateAsync(iteration.Id, abortCt);
                return gate is null or { State: ResearchIterationState.Stopped or ResearchIterationState.Paused };
            },
            log: (msg, logCt) => AppendHypothesisLogAsync(hypothesisId, msg, "HypothesisService", logCt),
            cancellationToken: cancellationToken);

        _logger.LogInformation("AI agent returned in {ElapsedMs}ms over {Runs} run(s)", loop.ElapsedMs, loop.RunsUsed);

        var parsed = AiResponseParser.ParseHypothesisResponse(loop.LastResponse);

        if (parsed != null)
        {
            return new HypothesisGenerationResult(
                parsed.Description,
                parsed.Optimize_sql,
                parsed.Revert_sql,
                loop.ElapsedMs);
        }

        // Fallback: treat entire response as description (legacy behavior)
        _logger.LogWarning("Could not parse structured JSON from AI response, falling back to raw text");
        return new HypothesisGenerationResult(loop.LastResponse ?? "(no response)", null, null, loop.ElapsedMs);
    }

    /// <summary>
    /// Builds the AI tool list for hypothesis generation. In analyze-only mode the
    /// DDL/DML tool is not exposed at all and query tools run behind the read-only guard.
    /// </summary>
    private static List<AITool> BuildAgentTools(
        SqlToolWrapper sqlTools,
        SchemaInspectionToolWrapper schemaTools,
        PerformanceMetricsToolWrapper perfTools,
        WebSearchToolWrapper? webTools,
        AgentTaskToolWrapper taskTools,
        BenchmarkRunToolWrapper benchmarkTools,
        bool analyzeOnly)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sqlTools.ExecuteSqlQuery, nameof(sqlTools.ExecuteSqlQuery)),
            AIFunctionFactory.Create(sqlTools.GetExecutionPlan, nameof(sqlTools.GetExecutionPlan)),
            // Primitive schema inspection fallback tools
            AIFunctionFactory.Create(schemaTools.GetObjectDefinition, nameof(schemaTools.GetObjectDefinition)),
            AIFunctionFactory.Create(schemaTools.GetObjectDependencies, nameof(schemaTools.GetObjectDependencies)),
            AIFunctionFactory.Create(schemaTools.GetObjectParameters, nameof(schemaTools.GetObjectParameters)),
            AIFunctionFactory.Create(schemaTools.GetObjectColumns, nameof(schemaTools.GetObjectColumns)),
            AIFunctionFactory.Create(schemaTools.GetTableIndexes, nameof(schemaTools.GetTableIndexes)),
            AIFunctionFactory.Create(schemaTools.GetTableStorage, nameof(schemaTools.GetTableStorage)),
            AIFunctionFactory.Create(schemaTools.GetTriggerInfo, nameof(schemaTools.GetTriggerInfo)),
            AIFunctionFactory.Create(schemaTools.GetSynonymTarget, nameof(schemaTools.GetSynonymTarget)),
            // Performance metric tools (read-only DMV queries)
            AIFunctionFactory.Create(perfTools.GetMissingIndexes, nameof(perfTools.GetMissingIndexes)),
            AIFunctionFactory.Create(perfTools.GetIndexFragmentation, nameof(perfTools.GetIndexFragmentation)),
            AIFunctionFactory.Create(perfTools.GetIndexUsageStats, nameof(perfTools.GetIndexUsageStats)),
            AIFunctionFactory.Create(perfTools.GetStatisticsHealth, nameof(perfTools.GetStatisticsHealth)),
            AIFunctionFactory.Create(perfTools.GetTopQueries, nameof(perfTools.GetTopQueries)),
            AIFunctionFactory.Create(perfTools.GetStoredProcedureStats, nameof(perfTools.GetStoredProcedureStats)),
            AIFunctionFactory.Create(perfTools.GetWaitStatistics, nameof(perfTools.GetWaitStatistics)),
            AIFunctionFactory.Create(perfTools.GetTableSizes, nameof(perfTools.GetTableSizes)),
            AIFunctionFactory.Create(perfTools.GetDatabaseConfiguration, nameof(perfTools.GetDatabaseConfiguration)),
        };

        if (!analyzeOnly)
            tools.Insert(1, AIFunctionFactory.Create(sqlTools.ExecuteSqlNonQuery, nameof(sqlTools.ExecuteSqlNonQuery)));

        if (webTools is not null)
        {
            tools.Add(AIFunctionFactory.Create(webTools.WebSearch, nameof(webTools.WebSearch)));
            tools.Add(AIFunctionFactory.Create(webTools.FetchWebPage, nameof(webTools.FetchWebPage)));
        }

        tools.Add(AIFunctionFactory.Create(benchmarkTools.GetBenchmarkRunDetails, nameof(benchmarkTools.GetBenchmarkRunDetails)));
        tools.Add(AIFunctionFactory.Create(benchmarkTools.GetBenchmarkRunPlanXml, nameof(benchmarkTools.GetBenchmarkRunPlanXml)));

        tools.Add(AIFunctionFactory.Create(taskTools.AddTask, nameof(taskTools.AddTask)));
        tools.Add(AIFunctionFactory.Create(taskTools.UpdateTask, nameof(taskTools.UpdateTask)));
        tools.Add(AIFunctionFactory.Create(taskTools.ListTasks, nameof(taskTools.ListTasks)));

        return tools;
    }

    /// <summary>Creates web search tools when an API key is configured; otherwise null.</summary>
    private WebSearchToolWrapper? CreateWebSearchTools() =>
        settings.Value.WebSearch.IsConfigured
            ? new WebSearchToolWrapper(settings.Value.WebSearch, loggerFactory.CreateLogger<WebSearchToolWrapper>())
            : null;

    private static string TruncateForLog(string message, int maxChars = MaxLogMessageChars)
    {
        if (message.Length <= maxChars)
            return message;
        return message[..maxChars] + "\n… (truncated)";
    }

    private async Task AppendHypothesisLogAsync(
        HypothesisId hypothesisId,
        string message,
        string? source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var logNow = DateTime.UtcNow;
            db.HypothesisLogs.Add(new HypothesisLog
            {
                HypothesisId = hypothesisId,
                Message = TruncateForLog(message),
                Source = source,
                CreatedAt = logNow,
                ModifiedAt = logNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await ModifiedAtStamping.StampHypothesisAndParentIterationAsync(db, hypothesisId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append hypothesis log for {HypothesisId}", hypothesisId);
        }
    }

    #region Combined Optimization

    private async Task RunCombinedOptimizationAsync(
        ResearchIterationId iterationId,
        ResearchIteration iteration,
        BenchmarkRun? baseline,
        CancellationToken ct)
    {
        var completedHypotheses = await GetCompletedHypothesesAsync(iterationId, ct);
        var successful = completedHypotheses
            .Where(h => h.Status == HypothesisState.Completed && h.ImpovementPercentage > 0)
            .ToList();

        if (successful.Count < 2)
        {
            _logger.LogInformation("Skipping combined optimization: only {Count} successful hypotheses (need at least 2)", successful.Count);
            return;
        }

        _logger.LogInformation("Running combined optimization with {Count} successful hypotheses", successful.Count);
        await UpdateIterationMessageAsync(iterationId, "Generating combined optimization...", ct);

        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded.");
        var aiConnection = iteration.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded.");

        var bestHypothesis = successful.OrderByDescending(h => h.ImpovementPercentage).First();

        var placeholder = await InsertPendingHypothesisAsync(iterationId, -1, bestHypothesis.Id, ct);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            await db.Hypotheses
                .Where(h => h.Id == placeholder.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Description, "Combined optimization (generating...)")
                    .SetProperty(h => h.ModifiedAt, now), ct);
            await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, placeholder.Id, ct);
        }

        try
        {
            await UpdateHypothesisStatusAsync(placeholder.Id, HypothesisState.Generating, ct);
            await AppendHypothesisLogAsync(placeholder.Id,
                $"Generating combined optimization from {successful.Count} successful hypotheses.",
                "HypothesisService", ct);

            var analyzeOnly = dbConnection.AnalyzeOnly;

            var executor = DatabaseExecutorFactory.Create(
                new BenchmarkConfig { DatabaseType = "MSSQL" },
                msg => _logger.LogDebug("{SqlLog}", msg));

            await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, ct);
            using var toolWrapper = new SqlToolWrapper(
                executor, conn, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<SqlToolWrapper>(), readOnly: analyzeOnly);
            using var schemaTools = new SchemaInspectionToolWrapper(
                executor, conn, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<SchemaInspectionToolWrapper>());
            using var perfTools = new PerformanceMetricsToolWrapper(
                executor, conn, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<PerformanceMetricsToolWrapper>());
            using var webTools = CreateWebSearchTools();
            var taskTools = new AgentTaskToolWrapper(
                AgentTaskScope.ForHypothesis(placeholder.Id),
                scopeFactory, loggerFactory.CreateLogger<AgentTaskToolWrapper>());
            var benchmarkTools = new BenchmarkRunToolWrapper(
                iterationId, scopeFactory, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<BenchmarkRunToolWrapper>());

            var tools = BuildAgentTools(toolWrapper, schemaTools, perfTools, webTools, taskTools, benchmarkTools, analyzeOnly);

            var maxRuns = Math.Clamp(settings.Value.MaxAgentContinuations, 1, 100);
            var combinedPrompt = HypothesisPromptBuilder.BuildCombinedPrompt(
                completedHypotheses,
                iteration.SchemaDiscoveryMarkdown,
                baselinePerformanceSummary: baseline is null ? null : HypothesisPromptBuilder.FormatBenchmarkRunSummary(baseline));

            var agent = agentFactory.Create(aiConnection,
                "You are a MSSQL performance optimization expert. Combine the most effective strategies into one ultimate optimization.\n\n" +
                AgentTaskPromptSection.Build(maxRuns), tools);

            var loop = await taskLoopRunner.RunAsync(
                agent,
                combinedPrompt,
                AgentTaskScope.ForHypothesis(placeholder.Id),
                isResponseAcceptable: r => AiResponseParser.ParseHypothesisResponse(r) != null,
                shouldAbort: async abortCt =>
                {
                    var gate = await TryGetIterationRunGateAsync(iterationId, abortCt);
                    return gate is null or { State: ResearchIterationState.Stopped or ResearchIterationState.Paused };
                },
                log: (msg, logCt) => AppendHypothesisLogAsync(placeholder.Id, msg, "HypothesisService", logCt),
                cancellationToken: ct);

            var parsed = AiResponseParser.ParseHypothesisResponse(loop.LastResponse);

            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Optimize_sql))
            {
                await FinalizeHypothesisAsync(placeholder.Id,
                    parsed.Description ?? "Combined optimization",
                    parsed.Optimize_sql, parsed.Revert_sql,
                    loop.ElapsedMs, ct);

                // Test the combined hypothesis
                if (baseline != null)
                {
                    iteration = await LoadIterationAsync(iterationId, ct)
                        ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared.");

                    await hypothesisTestingService.TestHypothesisAsync(
                        placeholder.Id, iteration, baseline,
                        (hid, msg, src) => AppendHypothesisLogAsync(hid, msg, src, CancellationToken.None).GetAwaiter().GetResult(),
                        ct);
                }
            }
            else
            {
                await FinalizeHypothesisAsync(placeholder.Id,
                    parsed?.Description ?? loop.LastResponse ?? "(no response)",
                    null, null, loop.ElapsedMs, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Combined optimization failed for iteration {IterationId}", iterationId);
            await AppendHypothesisLogAsync(placeholder.Id,
                $"Combined optimization failed: {TruncateForLog(ex.ToString())}",
                "HypothesisService", CancellationToken.None);
            await FailHypothesisAsync(placeholder.Id, ex.Message, CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<Hypothesis>> GetCompletedHypothesesAsync(
        ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses.AsNoTracking()
            .Include(h => h.BenchmarkRunAfter)
            .Where(h => h.ResearchIterationId == iterationId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
    }

    #endregion

    #region Schema Discovery

    private async Task RunSchemaDiscoveryAsync(ResearchIteration iteration, CancellationToken ct)
    {
        var experiment = iteration.Experiment;
        var dbConnection = experiment?.DatabaseConnection;
        if (experiment == null || dbConnection == null || string.IsNullOrWhiteSpace(experiment.BenchmarkSql))
        {
            _logger.LogWarning("Skipping schema discovery: missing experiment, DB connection, or benchmark SQL");
            return;
        }

        _logger.LogInformation("Running deterministic schema discovery for iteration {IterationId}", iteration.Id);

        await UpdateIterationMessageAsync(iteration.Id, "Running schema discovery...", ct);

        var executor = DatabaseExecutorFactory.Create(
            new BenchmarkConfig { DatabaseType = "MSSQL" },
            msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, ct);
        var discoveryResult = await schemaDiscoveryService.DiscoverSqlContextAsync(
            experiment.BenchmarkSql, conn, ct);

        var resultJson = System.Text.Json.JsonSerializer.Serialize(discoveryResult);
        var baseTables = SchemaDiscoveryService.SerializeBaseTables(discoveryResult.BaseTables);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.ResearchIterations
            .Where(r => r.Id == iteration.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SchemaDiscoveryMarkdown, discoveryResult.MarkdownSummary)
                .SetProperty(r => r.SchemaDiscoveryResultJson, resultJson)
                .SetProperty(r => r.RegisteredBaseTables, baseTables)
                .SetProperty(r => r.ModifiedAt, now), ct);

        // Update in-memory object so the rest of the loop sees the data
        iteration.SchemaDiscoveryMarkdown = discoveryResult.MarkdownSummary;
        iteration.SchemaDiscoveryResultJson = resultJson;
        iteration.RegisteredBaseTables = baseTables;

        _logger.LogInformation("Schema discovery stored: {Objects} objects, {Tables} base tables",
            discoveryResult.Objects.Count, discoveryResult.BaseTables.Count);
    }

    #endregion

    #region Database helpers

    private async Task<BenchmarkRun?> LoadBenchmarkRunAsync(BenchmarkRunId id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.BenchmarkRuns.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    private async Task<ResearchIteration?> LoadIterationAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.ResearchIterations
            .AsNoTracking()
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.DatabaseConnection)
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.AIConnection)
            .Include(b => b.AIConnection)
            .Include(b => b.Hypotheses)
            .FirstOrDefaultAsync(b => b.Id == iterationId, ct);
    }

    private sealed record IterationRunGate(ResearchIterationState State, int MaxNumberOfHypotheses);

    /// <summary>
    /// Loads current iteration state and cap. Returns null if the row no longer exists (do not treat as Stopped).
    /// </summary>
    private async Task<IterationRunGate?> TryGetIterationRunGateAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.ResearchIterations
            .AsNoTracking()
            .Where(b => b.Id == iterationId)
            .Select(b => new IterationRunGate(b.State, b.MaxNumberOfHypotheses))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int> CountHypothesesForIterationAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses.CountAsync(h => h.ResearchIterationId == iterationId, ct);
    }

    /// <summary>
    /// Loads the analysis findings that proposed this experiment (linked via
    /// <see cref="AnalysisFinding.ProposedExperimentId"/>) so the optimization agent
    /// sees the evidence and recommended SQL the analysis already produced.
    /// </summary>
    private async Task<IReadOnlyList<AnalysisFinding>> GetRelatedFindingsAsync(ExperimentId experimentId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.AnalysisFindings
            .AsNoTracking()
            .Where(f => f.ProposedExperimentId == experimentId)
            .OrderByDescending(f => f.ImpactScore)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<Hypothesis>> GetPriorHypothesesAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses
            .AsNoTracking()
            .Include(h => h.BenchmarkRunBefore)
            .Include(h => h.BenchmarkRunAfter)
            .Where(h => h.ResearchIterationId == iterationId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
    }

    private async Task<Hypothesis> InsertPendingHypothesisAsync(ResearchIterationId iterationId, int number, HypothesisId? buildsOnHypothesisId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var hypothesis = new Hypothesis
        {
            ResearchIterationId = iterationId,
            Status = HypothesisState.Pending,
            Description = $"Generating hypothesis #{number}...",
            BuildsOnHypothesisId = buildsOnHypothesisId,
            CreatedAt = DateTime.UtcNow,
        };
        db.Hypotheses.Add(hypothesis);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await ModifiedAtStamping.TouchResearchIterationAsync(db, iterationId, ct);
        return hypothesis;
    }

    private async Task UpdateHypothesisStatusAsync(HypothesisId id, HypothesisState state, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, state)
                .SetProperty(x => x.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    private async Task FinalizeHypothesisAsync(
        HypothesisId id, string? description,
        string? optimizeSql, string? revertSql,
        long timeUsedMs, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.Hypotheses
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, HypothesisState.Generated)
                .SetProperty(x => x.Description, description)
                .SetProperty(x => x.OptimizeSql, optimizeSql)
                .SetProperty(x => x.RevertSql, revertSql)
                .SetProperty(x => x.TimeUsedMs, timeUsedMs)
                .SetProperty(x => x.ModifiedAt, now), ct);
        await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
    }

    private async Task FailHypothesisAsync(HypothesisId id, string errorMessage, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            await db.Hypotheses
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, HypothesisState.Failed)
                    .SetProperty(x => x.ErrorMessage, errorMessage)
                    .SetProperty(x => x.ModifiedAt, now), ct);
            await ModifiedAtStamping.TouchResearchIterationForHypothesisAsync(db, id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark hypothesis {HypothesisId} as failed", id);
        }
    }

    private async Task UpdateIterationMessageAsync(ResearchIterationId iterationId, string message, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            await db.ResearchIterations
                .Where(b => b.Id == iterationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.LastMessage, message)
                    .SetProperty(b => b.ModifiedAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update research iteration {IterationId} message", iterationId);
        }
    }

    #endregion
}
