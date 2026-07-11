using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Runs a full database analysis (discovery): deterministic metric collection,
/// rule-based findings, then an AI deep dive with read-only tools. The target
/// database is never modified — analysis is always analyze-only.
/// </summary>
public sealed class DatabaseAnalysisService(
    AiAgentFactory agentFactory,
    PerformanceSnapshotService snapshotService,
    AgentTaskLoopRunner taskLoopRunner,
    IServiceScopeFactory scopeFactory,
    IOptions<OptimizeEngineSettings> settings,
    ILoggerFactory loggerFactory)
{
    private const int MaxLogMessageChars = 48_000;

    private readonly ILogger _logger = loggerFactory.CreateLogger<DatabaseAnalysisService>();

    public async Task RunAnalysisAsync(DatabaseAnalysisId analysisId, CancellationToken ct)
    {
        _logger.LogInformation("Starting database analysis {AnalysisId}", analysisId);

        var analysis = await LoadAnalysisAsync(analysisId, ct);
        if (analysis is null)
        {
            _logger.LogWarning("Database analysis {AnalysisId} not found, aborting", analysisId);
            return;
        }

        if (analysis.DatabaseConnection is null)
        {
            await FailAsync(analysisId, "No database connection configured.", CancellationToken.None);
            return;
        }

        try
        {
            await SetStateAsync(analysisId, DatabaseAnalysisState.Running, "Starting analysis...", ct, stampStartedAt: true);
            await AppendLogAsync(analysisId, "Analysis started. Target database is treated as READ-ONLY throughout.", "AnalysisService", ct);

            var executor = DatabaseExecutorFactory.Create(
                new BenchmarkConfig { DatabaseType = "MSSQL" },
                msg => _logger.LogDebug("{SqlLog}", msg));

            await using var conn = await executor.OpenConnectionAsync(analysis.DatabaseConnection.ConnectionString, ct);

            // ── Phase 1: deterministic metric collection ────────────────────
            await SetMessageAsync(analysisId, "Collecting performance metrics...", ct);
            var snapshot = snapshotService.Collect(executor, conn,
                progress => SetMessageAsync(analysisId, progress, CancellationToken.None).GetAwaiter().GetResult(),
                ct);

            var summaryMarkdown = PerformanceSnapshotService.BuildMarkdownSummary(snapshot);
            var snapshotJson = JsonSerializer.Serialize(snapshot);
            await StoreSnapshotAsync(analysisId, snapshotJson, summaryMarkdown, ct);
            await AppendLogAsync(analysisId,
                $"Metric collection complete: {snapshot.Sections.Count} sections, {snapshot.Errors.Count} collector errors." +
                (snapshot.Errors.Count > 0
                    ? "\nErrors:\n" + string.Join("\n", snapshot.Errors.Select(e => $"- {e.Key}: {e.Value}"))
                    : ""),
                "SnapshotService", ct);

            // ── Phase 2: rule-based findings ────────────────────────────────
            await SetMessageAsync(analysisId, "Deriving rule-based findings...", ct);
            var deterministicFindings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, analysisId);
            await InsertFindingsAsync(deterministicFindings, ct);
            await AppendLogAsync(analysisId,
                $"Recorded {deterministicFindings.Count} deterministic findings " +
                $"({deterministicFindings.Count(f => f.Severity == FindingSeverity.Good)} positive).",
                "SnapshotService", ct);

            if (await IsStoppedAsync(analysisId, ct))
            {
                await SetStateAsync(analysisId, DatabaseAnalysisState.Stopped, "Stopped by user.", CancellationToken.None);
                return;
            }

            // ── Phase 3: AI deep dive ───────────────────────────────────────
            if (analysis.AIConnection is not null)
            {
                await SetMessageAsync(analysisId, "AI deep dive in progress...", ct);
                var aiSummary = await RunAiDeepDiveAsync(analysis, executor, conn, summaryMarkdown, deterministicFindings, ct);
                if (!string.IsNullOrWhiteSpace(aiSummary))
                    await StoreAiSummaryAsync(analysisId, aiSummary, ct);
            }
            else
            {
                await AppendLogAsync(analysisId,
                    "No AI connection configured — skipping AI deep dive; only deterministic findings are available.",
                    "AnalysisService", ct);
            }

            var findingCount = await CountFindingsAsync(analysisId, ct);
            await SetStateAsync(analysisId, DatabaseAnalysisState.Completed,
                $"Analysis complete: {findingCount} findings.", CancellationToken.None, stampEndedAt: true);
            await AppendLogAsync(analysisId, $"Analysis completed with {findingCount} findings.", "AnalysisService", CancellationToken.None);

            _logger.LogInformation("Database analysis {AnalysisId} completed with {Count} findings", analysisId, findingCount);
        }
        catch (OperationCanceledException)
        {
            await SetStateAsync(analysisId, DatabaseAnalysisState.Stopped, "Cancelled.", CancellationToken.None, stampEndedAt: true);
            await AppendLogAsync(analysisId, "Analysis cancelled.", "AnalysisService", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database analysis {AnalysisId} failed", analysisId);
            await AppendLogAsync(analysisId, $"Analysis failed:\n{Truncate(ex.ToString())}", "AnalysisService", CancellationToken.None);
            await FailAsync(analysisId, ex.Message, CancellationToken.None);
        }
    }

    private async Task<string?> RunAiDeepDiveAsync(
        DatabaseAnalysis analysis,
        IDatabaseExecutor executor,
        System.Data.Common.DbConnection conn,
        string metricsSummaryMarkdown,
        IReadOnlyList<AnalysisFinding> deterministicFindings,
        CancellationToken ct)
    {
        var maxBytes = settings.Value.MaxToolResponseBytes;

        // All SQL tools run in read-only mode; ExecuteSqlNonQuery is not exposed at all.
        using var sqlTools = new SqlToolWrapper(
            executor, conn, maxBytes,
            loggerFactory.CreateLogger<SqlToolWrapper>(), readOnly: true);
        using var schemaTools = new SchemaInspectionToolWrapper(
            executor, conn, maxBytes,
            loggerFactory.CreateLogger<SchemaInspectionToolWrapper>());
        using var perfTools = new PerformanceMetricsToolWrapper(
            executor, conn, maxBytes,
            loggerFactory.CreateLogger<PerformanceMetricsToolWrapper>());
        var findingTools = new AnalysisFindingToolWrapper(
            analysis.Id, analysis.DatabaseConnectionId, analysis.AIConnectionId,
            scopeFactory, loggerFactory.CreateLogger<AnalysisFindingToolWrapper>());
        var taskTools = new AgentTaskToolWrapper(
            AgentTaskScope.ForAnalysis(analysis.Id),
            scopeFactory, loggerFactory.CreateLogger<AgentTaskToolWrapper>());

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sqlTools.ExecuteSqlQuery, nameof(sqlTools.ExecuteSqlQuery)),
            AIFunctionFactory.Create(sqlTools.GetExecutionPlan, nameof(sqlTools.GetExecutionPlan)),
            AIFunctionFactory.Create(schemaTools.GetObjectDefinition, nameof(schemaTools.GetObjectDefinition)),
            AIFunctionFactory.Create(schemaTools.GetObjectDependencies, nameof(schemaTools.GetObjectDependencies)),
            AIFunctionFactory.Create(schemaTools.GetObjectParameters, nameof(schemaTools.GetObjectParameters)),
            AIFunctionFactory.Create(schemaTools.GetObjectColumns, nameof(schemaTools.GetObjectColumns)),
            AIFunctionFactory.Create(schemaTools.GetTableIndexes, nameof(schemaTools.GetTableIndexes)),
            AIFunctionFactory.Create(schemaTools.GetTableStorage, nameof(schemaTools.GetTableStorage)),
            AIFunctionFactory.Create(schemaTools.GetTriggerInfo, nameof(schemaTools.GetTriggerInfo)),
            AIFunctionFactory.Create(schemaTools.GetSynonymTarget, nameof(schemaTools.GetSynonymTarget)),
            AIFunctionFactory.Create(perfTools.GetMissingIndexes, nameof(perfTools.GetMissingIndexes)),
            AIFunctionFactory.Create(perfTools.GetIndexFragmentation, nameof(perfTools.GetIndexFragmentation)),
            AIFunctionFactory.Create(perfTools.GetIndexUsageStats, nameof(perfTools.GetIndexUsageStats)),
            AIFunctionFactory.Create(perfTools.GetStatisticsHealth, nameof(perfTools.GetStatisticsHealth)),
            AIFunctionFactory.Create(perfTools.GetTopQueries, nameof(perfTools.GetTopQueries)),
            AIFunctionFactory.Create(perfTools.GetStoredProcedureStats, nameof(perfTools.GetStoredProcedureStats)),
            AIFunctionFactory.Create(perfTools.GetWaitStatistics, nameof(perfTools.GetWaitStatistics)),
            AIFunctionFactory.Create(perfTools.GetTableSizes, nameof(perfTools.GetTableSizes)),
            AIFunctionFactory.Create(perfTools.GetDatabaseConfiguration, nameof(perfTools.GetDatabaseConfiguration)),
            AIFunctionFactory.Create(perfTools.ListProceduresAndViews, nameof(perfTools.ListProceduresAndViews)),
            AIFunctionFactory.Create(findingTools.ReportFinding, nameof(findingTools.ReportFinding)),
            AIFunctionFactory.Create(findingTools.ProposeExperiment, nameof(findingTools.ProposeExperiment)),
            AIFunctionFactory.Create(taskTools.AddTask, nameof(taskTools.AddTask)),
            AIFunctionFactory.Create(taskTools.UpdateTask, nameof(taskTools.UpdateTask)),
            AIFunctionFactory.Create(taskTools.ListTasks, nameof(taskTools.ListTasks)),
        };

        WebSearchToolWrapper? webTools = null;
        var webSearchEnabled = analysis.EnableWebSearch && settings.Value.WebSearch.IsConfigured;
        if (webSearchEnabled)
        {
            webTools = new WebSearchToolWrapper(
                settings.Value.WebSearch,
                loggerFactory.CreateLogger<WebSearchToolWrapper>());
            tools.Add(AIFunctionFactory.Create(webTools.WebSearch, nameof(webTools.WebSearch)));
            tools.Add(AIFunctionFactory.Create(webTools.FetchWebPage, nameof(webTools.FetchWebPage)));
        }
        else if (analysis.EnableWebSearch)
        {
            await AppendLogAsync(analysis.Id,
                "Web search requested but no API key is configured (OptimizeEngine:WebSearch:ApiKey) — continuing without web tools.",
                "AnalysisService", ct);
        }

        try
        {
            var maxRuns = Math.Clamp(settings.Value.MaxAgentContinuations, 1, 100);
            var instructions = AnalysisPromptBuilder.BuildInstructions(analysis, webSearchEnabled, maxRuns);
            var prompt = AnalysisPromptBuilder.BuildPrompt(analysis, metricsSummaryMarkdown, deterministicFindings);

            var agent = agentFactory.Create(analysis.AIConnection!, instructions, tools);

            await AppendLogAsync(analysis.Id,
                $"Invoking AI agent ({analysis.AIConnection!.Provider}/{analysis.AIConnection.Model}) with {tools.Count} tools" +
                (webSearchEnabled ? " including web search" : "") +
                $"; task-loop limit {maxRuns} runs.",
                "AnalysisAgent", ct);

            var loop = await taskLoopRunner.RunAsync(
                agent,
                prompt,
                AgentTaskScope.ForAnalysis(analysis.Id),
                isResponseAcceptable: r => !string.IsNullOrWhiteSpace(r),
                shouldAbort: async abortCt => await IsStoppedAsync(analysis.Id, abortCt),
                log: (msg, logCt) => AppendLogAsync(analysis.Id, msg, "AnalysisAgent", logCt),
                cancellationToken: ct);

            await AppendLogAsync(analysis.Id,
                $"AI agent finished in {loop.ElapsedMs} ms over {loop.RunsUsed} run(s); " +
                $"{loop.OpenTaskCount} task(s) left open. Summary length: {loop.LastResponse?.Length ?? 0} chars.",
                "AnalysisAgent", ct);

            return loop.LastResponse;
        }
        finally
        {
            webTools?.Dispose();
        }
    }

    #region Database helpers

    private async Task<DatabaseAnalysis?> LoadAnalysisAsync(DatabaseAnalysisId id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.DatabaseAnalyses
            .AsNoTracking()
            .Include(a => a.DatabaseConnection)
            .Include(a => a.AIConnection)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    private async Task<bool> IsStoppedAsync(DatabaseAnalysisId id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var state = await db.DatabaseAnalyses.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => (DatabaseAnalysisState?)a.State)
            .FirstOrDefaultAsync(ct);
        return state is DatabaseAnalysisState.Stopped or null;
    }

    private async Task SetStateAsync(
        DatabaseAnalysisId id, DatabaseAnalysisState state, string message,
        CancellationToken ct, bool stampStartedAt = false, bool stampEndedAt = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.DatabaseAnalyses
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, state)
                .SetProperty(a => a.LastMessage, message)
                .SetProperty(a => a.StartedAt, a => stampStartedAt ? now : a.StartedAt)
                .SetProperty(a => a.EndedAt, a => stampEndedAt ? now : a.EndedAt)
                .SetProperty(a => a.ModifiedAt, now), ct);
    }

    private async Task SetMessageAsync(DatabaseAnalysisId id, string message, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            await db.DatabaseAnalyses
                .Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LastMessage, message)
                    .SetProperty(a => a.ModifiedAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update analysis {AnalysisId} message", id);
        }
    }

    private async Task FailAsync(DatabaseAnalysisId id, string message, CancellationToken ct)
    {
        try
        {
            await SetStateAsync(id, DatabaseAnalysisState.Failed, message, ct, stampEndedAt: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark analysis {AnalysisId} as failed", id);
        }
    }

    private async Task StoreSnapshotAsync(DatabaseAnalysisId id, string snapshotJson, string summaryMarkdown, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.DatabaseAnalyses
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.MetricsSnapshotJson, snapshotJson)
                .SetProperty(a => a.MetricsSummaryMarkdown, summaryMarkdown)
                .SetProperty(a => a.ModifiedAt, now), ct);
    }

    private async Task StoreAiSummaryAsync(DatabaseAnalysisId id, string summary, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.DatabaseAnalyses
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AiSummaryMarkdown, summary)
                .SetProperty(a => a.ModifiedAt, now), ct);
    }

    private async Task InsertFindingsAsync(IReadOnlyList<AnalysisFinding> findings, CancellationToken ct)
    {
        if (findings.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        db.AnalysisFindings.AddRange(findings);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<int> CountFindingsAsync(DatabaseAnalysisId id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.AnalysisFindings.CountAsync(f => f.DatabaseAnalysisId == id, ct);
    }

    private async Task AppendLogAsync(DatabaseAnalysisId id, string message, string? source, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            db.DatabaseAnalysisLogs.Add(new DatabaseAnalysisLog
            {
                Id = DatabaseAnalysisLogId.Transient,
                DatabaseAnalysisId = id,
                Message = Truncate(message),
                Source = source,
                CreatedAt = now,
                ModifiedAt = now,
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            await db.DatabaseAnalyses
                .Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ModifiedAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append analysis log for {AnalysisId}", id);
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxLogMessageChars ? message : message[..MaxLogMessageChars] + "\n… (truncated)";

    #endregion
}
