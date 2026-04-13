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
                    $"AI agent finished in {result.TimeUsedMs} ms. Output length: {result.Description?.Length ?? 0} characters.",
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

                await FinalizeHypothesisAsync(placeholder.Id, result.Description, result.TimeUsedMs, cancellationToken);
                await AppendHypothesisLogAsync(
                    placeholder.Id,
                    "Hypothesis finalized (Generated).",
                    "HypothesisService",
                    cancellationToken);
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
            $"All {hypothesesCreated} hypotheses generated",
            cancellationToken);

        _logger.LogInformation("Research iteration {IterationId} hypothesis loop completed ({Count} hypotheses)", iterationId, hypothesesCreated);
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

    private async Task<(string? Description, long TimeUsedMs)> GenerateSingleHypothesisAsync(
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

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlQuery, nameof(toolWrapper.ExecuteSqlQuery)),
            AIFunctionFactory.Create(toolWrapper.ExecuteSqlNonQuery, nameof(toolWrapper.ExecuteSqlNonQuery)),
            AIFunctionFactory.Create(toolWrapper.GetExecutionPlan, nameof(toolWrapper.GetExecutionPlan)),
        };

        var instructions = HypothesisPromptBuilder.BuildInstructions(experiment, iteration, priorHypotheses);
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

        return (result?.ToString() ?? "(no response)", sw.ElapsedMilliseconds);
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

    #region Database helpers

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

    private async Task FinalizeHypothesisAsync(HypothesisId id, string? description, long timeUsedMs, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        await db.Hypotheses
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, HypothesisState.Generated)
                .SetProperty(x => x.Description, description)
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
