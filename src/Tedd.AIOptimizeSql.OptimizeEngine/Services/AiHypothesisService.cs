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

        // Run baseline benchmark if not already done
        BenchmarkRun? baseline = null;
        if (iteration.BaselineBenchmarkRunId == null && !string.IsNullOrWhiteSpace(iteration.Experiment?.BenchmarkSql))
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

        var hypothesesCreated = iteration.Hypotheses.Count;
        var pendingRunStartedLog = runStartedLogLine;

        while (hypothesesCreated < iteration.MaxNumberOfHypotheses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentState = await GetIterationStateAsync(iterationId, cancellationToken);
            if (currentState is ResearchIterationState.Stopped or ResearchIterationState.Paused)
            {
                _logger.LogInformation("Research iteration {IterationId} is {State}, stopping generation loop", iterationId, currentState);
                await UpdateIterationMessageAsync(iterationId,
                    currentState == ResearchIterationState.Paused ? "Paused" : "Stopped by user",
                    cancellationToken);
                return;
            }

            await UpdateIterationMessageAsync(iterationId,
                $"Generating hypothesis {hypothesesCreated + 1} of {iteration.MaxNumberOfHypotheses}",
                cancellationToken);

            iteration = await LoadIterationAsync(iterationId, cancellationToken)
                ?? throw new InvalidOperationException($"Research iteration {iterationId} disappeared during processing.");

            var priorHypotheses = await GetPriorHypothesesAsync(iterationId, cancellationToken);

            var placeholder = await InsertPendingHypothesisAsync(iterationId, hypothesesCreated + 1, cancellationToken);

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
                $"Hypothesis record created (pending). Target slot {hypothesesCreated + 1} of {iteration.MaxNumberOfHypotheses}.",
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

                var result = await GenerateSingleHypothesisAsync(iteration, priorHypotheses, placeholder.Id, cancellationToken);

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

                // Test hypothesis if it has executable SQL and we have a baseline
                if (!string.IsNullOrWhiteSpace(result.OptimizeSql) && baseline != null)
                {
                    await AppendHypothesisLogAsync(
                        placeholder.Id,
                        "Starting Apply → Benchmark → Revert cycle.",
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

                hypothesesCreated++;

                _logger.LogInformation("Hypothesis #{Number} created for research iteration {IterationId}", hypothesesCreated, iterationId);
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
                    hypothesesCreated + 1, iterationId);
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    $"Generation failed:\n{TruncateForLog(ex.ToString())}",
                    "HypothesisService",
                    CancellationToken.None);
                await FailHypothesisAsync(placeholder.Id, ex.Message, CancellationToken.None);
                hypothesesCreated++;
            }

            var stateAfter = await GetIterationStateAsync(iterationId, cancellationToken);
            if (stateAfter is ResearchIterationState.Stopped or ResearchIterationState.Paused)
            {
                _logger.LogInformation("Research iteration {IterationId} is {State} after hypothesis, stopping", iterationId, stateAfter);
                await UpdateIterationMessageAsync(iterationId,
                    stateAfter == ResearchIterationState.Paused ? "Paused" : "Stopped by user",
                    CancellationToken.None);
                return;
            }
        }

        await UpdateIterationMessageAsync(iterationId,
            $"All {hypothesesCreated} hypotheses generated, checking for combined optimization...",
            cancellationToken);

        _logger.LogInformation("Research iteration {IterationId} hypothesis loop completed ({Count} hypotheses)", iterationId, hypothesesCreated);

        // Combined optimization: if 2+ hypotheses succeeded, ask AI to combine the best
        await RunCombinedOptimizationAsync(iterationId, iteration, baseline, cancellationToken);

        await UpdateIterationMessageAsync(iterationId,
            $"All {hypothesesCreated} hypotheses generated and tested",
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
        CancellationToken cancellationToken)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded on the research iteration.");
        var aiConnection = iteration.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded on the research iteration.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded on the experiment.");

        var executor = DatabaseExecutorFactory.Create(
            new BenchmarkConfig { DatabaseType = "MSSQL" },
            msg => _logger.LogDebug("{SqlLog}", msg));

        await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, cancellationToken);
        using var toolWrapper = new SqlToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<SqlToolWrapper>());
        using var schemaTools = new SchemaInspectionToolWrapper(
            executor, conn, settings.Value.MaxToolResponseBytes,
            loggerFactory.CreateLogger<SchemaInspectionToolWrapper>());

        var tools = new List<AITool>
        {
            // Existing SQL execution tools
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlQuery, nameof(toolWrapper.ExecuteSqlQuery)),
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlNonQuery, nameof(toolWrapper.ExecuteSqlNonQuery)),
            AIFunctionFactory.Create(toolWrapper.GetExecutionPlan, nameof(toolWrapper.GetExecutionPlan)),
            // Primitive schema inspection fallback tools
            AIFunctionFactory.Create(schemaTools.GetObjectDefinition, nameof(schemaTools.GetObjectDefinition)),
            AIFunctionFactory.Create(schemaTools.GetObjectDependencies, nameof(schemaTools.GetObjectDependencies)),
            AIFunctionFactory.Create(schemaTools.GetObjectParameters, nameof(schemaTools.GetObjectParameters)),
            AIFunctionFactory.Create(schemaTools.GetObjectColumns, nameof(schemaTools.GetObjectColumns)),
            AIFunctionFactory.Create(schemaTools.GetTableIndexes, nameof(schemaTools.GetTableIndexes)),
            AIFunctionFactory.Create(schemaTools.GetTableStorage, nameof(schemaTools.GetTableStorage)),
            AIFunctionFactory.Create(schemaTools.GetTriggerInfo, nameof(schemaTools.GetTriggerInfo)),
            AIFunctionFactory.Create(schemaTools.GetSynonymTarget, nameof(schemaTools.GetSynonymTarget)),
        };

        var instructions = HypothesisPromptBuilder.BuildInstructions(
            experiment, iteration, priorHypotheses,
            schemaDiscoveryMarkdown: iteration.SchemaDiscoveryMarkdown);

        var agent = agentFactory.Create(aiConnection, instructions, tools);

        var sw = Stopwatch.StartNew();
        var prompt = HypothesisPromptBuilder.BuildPrompt(iteration, priorHypotheses);

        _logger.LogInformation("Invoking AI agent for research iteration {IterationId}, hypothesis #{Number}",
            iteration.Id, priorHypotheses.Count + 1);

        await AppendHypothesisLogAsync(
            hypothesisId,
            $"Invoking AI agent (model context from iteration). Prior hypotheses in iteration: {priorHypotheses.Count}.",
            "HypothesisService",
            cancellationToken);

        var result = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        sw.Stop();

        _logger.LogInformation("AI agent returned in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        var rawResponse = result?.ToString();
        var parsed = AiResponseParser.ParseHypothesisResponse(rawResponse);

        if (parsed != null)
        {
            return new HypothesisGenerationResult(
                parsed.Description,
                parsed.Optimize_sql,
                parsed.Revert_sql,
                sw.ElapsedMilliseconds);
        }

        // Fallback: treat entire response as description (legacy behavior)
        _logger.LogWarning("Could not parse structured JSON from AI response, falling back to raw text");
        return new HypothesisGenerationResult(rawResponse ?? "(no response)", null, null, sw.ElapsedMilliseconds);
    }

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
            db.HypothesisLogs.Add(new HypothesisLog
            {
                HypothesisId = hypothesisId,
                Message = TruncateForLog(message),
                Source = source,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
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

        var placeholder = await InsertPendingHypothesisAsync(iterationId, -1, ct);

        // Mark as building on best individual hypothesis
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            await db.Hypotheses
                .Where(h => h.Id == placeholder.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.BuildsOnHypothesisId, bestHypothesis.Id)
                    .SetProperty(h => h.Description, "Combined optimization (generating...)"), ct);
        }

        try
        {
            await UpdateHypothesisStatusAsync(placeholder.Id, HypothesisState.Generating, ct);
            await AppendHypothesisLogAsync(placeholder.Id,
                $"Generating combined optimization from {successful.Count} successful hypotheses.",
                "HypothesisService", ct);

            var executor = DatabaseExecutorFactory.Create(
                new BenchmarkConfig { DatabaseType = "MSSQL" },
                msg => _logger.LogDebug("{SqlLog}", msg));

            await using var conn = await executor.OpenConnectionAsync(dbConnection.ConnectionString, ct);
            using var toolWrapper = new SqlToolWrapper(
                executor, conn, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<SqlToolWrapper>());
            using var schemaTools = new SchemaInspectionToolWrapper(
                executor, conn, settings.Value.MaxToolResponseBytes,
                loggerFactory.CreateLogger<SchemaInspectionToolWrapper>());

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(toolWrapper.ExecuteSqlQuery, nameof(toolWrapper.ExecuteSqlQuery)),
                AIFunctionFactory.Create(toolWrapper.ExecuteSqlNonQuery, nameof(toolWrapper.ExecuteSqlNonQuery)),
                AIFunctionFactory.Create(toolWrapper.GetExecutionPlan, nameof(toolWrapper.GetExecutionPlan)),
                AIFunctionFactory.Create(schemaTools.GetObjectDefinition, nameof(schemaTools.GetObjectDefinition)),
                AIFunctionFactory.Create(schemaTools.GetObjectDependencies, nameof(schemaTools.GetObjectDependencies)),
                AIFunctionFactory.Create(schemaTools.GetTableIndexes, nameof(schemaTools.GetTableIndexes)),
                AIFunctionFactory.Create(schemaTools.GetTableStorage, nameof(schemaTools.GetTableStorage)),
            };

            var combinedPrompt = HypothesisPromptBuilder.BuildCombinedPrompt(
                completedHypotheses,
                iteration.SchemaDiscoveryMarkdown);

            var agent = agentFactory.Create(aiConnection,
                "You are a MSSQL performance optimization expert. Combine the most effective strategies into one ultimate optimization.", tools);

            var sw = Stopwatch.StartNew();
            var result = await agent.RunAsync(combinedPrompt, cancellationToken: ct);
            sw.Stop();

            var parsed = AiResponseParser.ParseHypothesisResponse(result?.ToString());

            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Optimize_sql))
            {
                await FinalizeHypothesisAsync(placeholder.Id,
                    parsed.Description ?? "Combined optimization",
                    parsed.Optimize_sql, parsed.Revert_sql,
                    sw.ElapsedMilliseconds, ct);

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
                    parsed?.Description ?? result?.ToString() ?? "(no response)",
                    null, null, sw.ElapsedMilliseconds, ct);
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
        await db.ResearchIterations
            .Where(r => r.Id == iteration.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SchemaDiscoveryMarkdown, discoveryResult.MarkdownSummary)
                .SetProperty(r => r.SchemaDiscoveryResultJson, resultJson)
                .SetProperty(r => r.RegisteredBaseTables, baseTables), ct);

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

    private async Task<ResearchIterationState> GetIterationStateAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.ResearchIterations
            .Where(b => b.Id == iterationId)
            .Select(b => b.State)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyList<Hypothesis>> GetPriorHypothesesAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses
            .AsNoTracking()
            .Where(h => h.ResearchIterationId == iterationId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
    }

    private async Task<Hypothesis> InsertPendingHypothesisAsync(ResearchIterationId iterationId, int number, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var hypothesis = new Hypothesis
        {
            ResearchIterationId = iterationId,
            Status = HypothesisState.Pending,
            Description = $"Generating hypothesis #{number}...",
            CreatedAt = DateTime.UtcNow,
        };
        db.Hypotheses.Add(hypothesis);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return hypothesis;
    }

    private async Task UpdateHypothesisStatusAsync(HypothesisId id, HypothesisState state, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, state), ct);
    }

    private async Task FinalizeHypothesisAsync(
        HypothesisId id, string? description,
        string? optimizeSql, string? revertSql,
        long timeUsedMs, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, HypothesisState.Generated)
                .SetProperty(x => x.Description, description)
                .SetProperty(x => x.OptimizeSql, optimizeSql)
                .SetProperty(x => x.RevertSql, revertSql)
                .SetProperty(x => x.TimeUsedMs, timeUsedMs), ct);
    }

    private async Task FailHypothesisAsync(HypothesisId id, string errorMessage, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            await db.Hypotheses
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, HypothesisState.Failed)
                    .SetProperty(x => x.ErrorMessage, errorMessage), ct);
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
            await db.ResearchIterations
                .Where(b => b.Id == iterationId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastMessage, message), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update research iteration {IterationId} message", iterationId);
        }
    }

    #endregion
}
