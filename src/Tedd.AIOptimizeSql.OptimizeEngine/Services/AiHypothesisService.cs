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

    public async Task RunBatchAsync(HypothesisBatchId batchId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting hypothesis generation loop for batch {BatchId}", batchId);

        var batch = await LoadBatchAsync(batchId, cancellationToken);
        if (batch is null)
        {
            _logger.LogWarning("Batch {BatchId} not found, aborting", batchId);
            return;
        }

        var hypothesesCreated = batch.Hypotheses.Count;

        while (hypothesesCreated < batch.MaxNumberOfHypotheses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentState = await GetBatchStateAsync(batchId, cancellationToken);
            if (currentState is HypothesisBatchState.Stopped or HypothesisBatchState.Paused)
            {
                _logger.LogInformation("Batch {BatchId} is {State}, stopping generation loop", batchId, currentState);
                await UpdateBatchMessageAsync(batchId,
                    currentState == HypothesisBatchState.Paused ? "Paused" : "Stopped by user",
                    cancellationToken);
                return;
            }

            await UpdateBatchMessageAsync(batchId,
                $"Generating hypothesis {hypothesesCreated + 1} of {batch.MaxNumberOfHypotheses}",
                cancellationToken);

            batch = await LoadBatchAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException($"Batch {batchId} disappeared during processing.");

            var priorHypotheses = await GetPriorHypothesesAsync(batchId, cancellationToken);

            var placeholder = await InsertPendingHypothesisAsync(batchId, hypothesesCreated + 1, cancellationToken);

            try
            {
                await UpdateHypothesisStatusAsync(placeholder.Id, HypothesisState.Generating, cancellationToken);

                var result = await GenerateSingleHypothesisAsync(batch, priorHypotheses, cancellationToken);

                await FinalizeHypothesisAsync(placeholder.Id, result.Description, result.TimeUsedMs, cancellationToken);
                hypothesesCreated++;

                _logger.LogInformation("Hypothesis #{Number} created for batch {BatchId}", hypothesesCreated, batchId);
            }
            catch (OperationCanceledException)
            {
                await FailHypothesisAsync(placeholder.Id, "Cancelled", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate hypothesis #{Number} for batch {BatchId}",
                    hypothesesCreated + 1, batchId);
                await FailHypothesisAsync(placeholder.Id, ex.Message, CancellationToken.None);
                hypothesesCreated++;
            }

            var stateAfter = await GetBatchStateAsync(batchId, cancellationToken);
            if (stateAfter is HypothesisBatchState.Stopped or HypothesisBatchState.Paused)
            {
                _logger.LogInformation("Batch {BatchId} is {State} after hypothesis, stopping", batchId, stateAfter);
                await UpdateBatchMessageAsync(batchId,
                    stateAfter == HypothesisBatchState.Paused ? "Paused" : "Stopped by user",
                    CancellationToken.None);
                return;
            }
        }

        await UpdateBatchMessageAsync(batchId,
            $"All {hypothesesCreated} hypotheses generated",
            cancellationToken);

        _logger.LogInformation("Batch {BatchId} hypothesis loop completed ({Count} hypotheses)", batchId, hypothesesCreated);
    }

    private async Task<(string? Description, long TimeUsedMs)> GenerateSingleHypothesisAsync(
        HypothesisBatch batch,
        IReadOnlyList<Hypothesis> priorHypotheses,
        CancellationToken cancellationToken)
    {
        var experiment = batch.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded on the batch.");
        var aiConnection = batch.AIConnection
            ?? throw new InvalidOperationException("AIConnection must be loaded on the batch.");
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

        var instructions = HypothesisPromptBuilder.BuildInstructions(experiment, batch, priorHypotheses);
        var agent = agentFactory.Create(aiConnection, instructions, tools);

        var sw = Stopwatch.StartNew();
        var prompt = HypothesisPromptBuilder.BuildPrompt(batch, priorHypotheses);

        _logger.LogInformation("Invoking AI agent for batch {BatchId}, hypothesis #{Number}",
            batch.Id, priorHypotheses.Count + 1);

        var result = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        sw.Stop();

        _logger.LogInformation("AI agent returned in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        return (result?.ToString() ?? "(no response)", sw.ElapsedMilliseconds);
    }

    #region Database helpers

    private async Task<HypothesisBatch?> LoadBatchAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.HypothesisBatches
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.DatabaseConnection)
            .Include(b => b.Experiment!)
                .ThenInclude(e => e.AIConnection)
            .Include(b => b.AIConnection)
            .Include(b => b.Hypotheses)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
    }

    private async Task<HypothesisBatchState> GetBatchStateAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.HypothesisBatches
            .Where(b => b.Id == batchId)
            .Select(b => b.State)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyList<Hypothesis>> GetPriorHypothesesAsync(HypothesisBatchId batchId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        return await db.Hypotheses
            .AsNoTracking()
            .Where(h => h.HypothesisBatchId == batchId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
    }

    private async Task<Hypothesis> InsertPendingHypothesisAsync(HypothesisBatchId batchId, int number, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var hypothesis = new Hypothesis
        {
            HypothesisBatchId = batchId,
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

    private async Task UpdateBatchMessageAsync(HypothesisBatchId batchId, string message, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
            if (batch is not null)
            {
                batch.LastMessage = message;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update batch {BatchId} message", batchId);
        }
    }

    #endregion
}
