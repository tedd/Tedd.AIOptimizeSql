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
    private readonly ILogger _logger = loggerFactory.CreateLogger<AiHypothesisService>();

    public async Task RunIterationAsync(ResearchIterationId iterationId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting hypothesis generation loop for research iteration {IterationId}", iterationId);

        var iteration = await LoadIterationAsync(iterationId, cancellationToken);
        if (iteration is null)
        {
            _logger.LogWarning("Research iteration {IterationId} not found, aborting", iterationId);
            return;
        }

        var hypothesesCreated = iteration.Hypotheses.Count;

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

            try
            {
                await UpdateHypothesisStatusAsync(placeholder.Id, HypothesisState.Generating, cancellationToken);

                var result = await GenerateSingleHypothesisAsync(iteration, priorHypotheses, cancellationToken);

                await FinalizeHypothesisAsync(placeholder.Id, result.Description, result.TimeUsedMs, cancellationToken);
                hypothesesCreated++;

                _logger.LogInformation("Hypothesis #{Number} created for research iteration {IterationId}", hypothesesCreated, iterationId);
            }
            catch (OperationCanceledException)
            {
                await FailHypothesisAsync(placeholder.Id, "Cancelled", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate hypothesis #{Number} for research iteration {IterationId}",
                    hypothesesCreated + 1, iterationId);
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

    private async Task<(string? Description, long TimeUsedMs)> GenerateSingleHypothesisAsync(
        ResearchIteration iteration,
        IReadOnlyList<Hypothesis> priorHypotheses,
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

        var result = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        sw.Stop();

        _logger.LogInformation("AI agent returned in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        return (result?.ToString() ?? "(no response)", sw.ElapsedMilliseconds);
    }

    #region Database helpers

    private async Task<ResearchIteration?> LoadIterationAsync(ResearchIterationId iterationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.ResearchIterations
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
        return hypothesis;
    }

    private async Task UpdateHypothesisStatusAsync(HypothesisId id, HypothesisState state, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var h = await db.Hypotheses.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is not null)
        {
            h.Status = state;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task FinalizeHypothesisAsync(HypothesisId id, string? description, long timeUsedMs, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var h = await db.Hypotheses.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is not null)
        {
            h.Status = HypothesisState.Generated;
            h.Description = description;
            h.TimeUsedMs = timeUsedMs;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task FailHypothesisAsync(HypothesisId id, string errorMessage, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var h = await db.Hypotheses.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is not null)
            {
                h.Status = HypothesisState.Failed;
                h.ErrorMessage = errorMessage;
                await db.SaveChangesAsync(ct);
            }
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
            var iteration = await db.ResearchIterations.FirstOrDefaultAsync(b => b.Id == iterationId, ct);
            if (iteration is not null)
            {
                iteration.LastMessage = message;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update research iteration {IterationId} message", iterationId);
        }
    }

    #endregion
}
