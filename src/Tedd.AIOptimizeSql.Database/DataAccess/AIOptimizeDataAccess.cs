using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public sealed class AIOptimizeDataAccess(AIOptimizeDbContext db) : IAIOptimizeDataAccess
{
    public async Task<(IReadOnlyList<HypothesisBatchListRow> Items, int TotalCount)> GetHypothesisBatchesPageAsync(
        int skip,
        int take,
        string? sortLabel,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken = default)
    {
        var query = db.HypothesisBatches.AsNoTracking();

        var descending = sortDirection == ListSortDirection.Descending;
        query = sortLabel switch
        {
            "Experiment" => descending
                ? query.OrderByDescending(b => b.Experiment!.Name)
                : query.OrderBy(b => b.Experiment!.Name),
            "Hypotheses" => descending
                ? query.OrderByDescending(b => b.Hypotheses.Count)
                : query.OrderBy(b => b.Hypotheses.Count),
            "State" => descending
                ? query.OrderByDescending(b => b.State)
                : query.OrderBy(b => b.State),
            "StartedAt" => descending
                ? query.OrderByDescending(b => b.StartedAt)
                : query.OrderBy(b => b.StartedAt),
            "EndedAt" => descending
                ? query.OrderByDescending(b => b.EndedAt)
                : query.OrderBy(b => b.EndedAt),
            "CreatedAt" => descending
                ? query.OrderByDescending(b => b.CreatedAt)
                : query.OrderBy(b => b.CreatedAt),
            "Id" => descending
                ? query.OrderByDescending(b => b.Id)
                : query.OrderBy(b => b.Id),
            _ => query.OrderByDescending(b => b.CreatedAt)
        };

        var total = await query.CountAsync(cancellationToken);

        var page = await query
            .Skip(skip)
            .Take(take)
            .Select(b => new HypothesisBatchListRow(
                b.Id,
                b.ExperimentId,
                b.Experiment!.Name,
                b.Hypotheses.Count,
                b.Hypotheses.Select(h => (double?)h.ImpovementPercentage).Min(),
                b.Hypotheses.Count(h => h.ImpovementPercentage < 0),
                b.State,
                b.StartedAt,
                b.EndedAt,
                b.LastMessage,
                b.Hints,
                b.CreatedAt,
                b.AiModelUsed))
            .ToListAsync(cancellationToken);

        return (page, total);
    }

    public Task<HypothesisBatch?> GetHypothesisBatchForEditAsync(HypothesisBatchId id, CancellationToken cancellationToken = default) =>
        db.HypothesisBatches
            .Include(b => b.Experiment)
            .Include(b => b.AIConnection)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<HypothesisBatchId> CreateHypothesisBatchAsync(
        ExperimentId experimentId,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        var batch = new HypothesisBatch
        {
            ExperimentId = experimentId,
            Hints = hints,
            MaxNumberOfHypotheses = maxNumberOfHypotheses,
            State = HypothesisBatchState.Stopped,
            CreatedAt = DateTime.UtcNow
        };
        db.HypothesisBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }

    public async Task UpdateHypothesisBatchEditableFieldsAsync(
        HypothesisBatchId id,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                    ?? throw new InvalidOperationException($"Hypothesis batch {id} was not found.");
        batch.Hints = hints;
        batch.MaxNumberOfHypotheses = maxNumberOfHypotheses;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetHypothesisBatchStateAsync(
        HypothesisBatchId id,
        HypothesisBatchState state,
        CancellationToken cancellationToken = default)
    {
        var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                    ?? throw new InvalidOperationException($"Hypothesis batch {id} was not found.");
        batch.State = state;
        await SyncRunQueueForBatchAsync(id, state, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task BeginHypothesisBatchRunAsync(HypothesisBatchId id, CancellationToken cancellationToken = default)
    {
        var batch = await db.HypothesisBatches
                       .Include(b => b.Experiment!)
                       .ThenInclude(e => e.AIConnection)
                       .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                   ?? throw new InvalidOperationException($"Hypothesis batch {id} was not found.");

        ApplyAiSnapshotFromExperiment(batch, batch.Experiment!);
        batch.State = HypothesisBatchState.Running;
        batch.StartedAt = DateTime.UtcNow;
        batch.EndedAt = null;
        batch.LastMessage = "Run started";

        await RemoveRunQueueEntriesForBatchAsync(id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteHypothesisBatchAsync(HypothesisBatchId id, CancellationToken cancellationToken = default)
    {
        var batch = await db.HypothesisBatches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (batch is null)
            return;
        db.HypothesisBatches.Remove(batch);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAiConnectionReferencesAsync(AIConnectionId id, CancellationToken cancellationToken = default)
    {
        var experiments = await db.Experiments.Where(e => e.AIConnectionId == id).ToListAsync(cancellationToken);
        foreach (var e in experiments)
        {
            e.AIConnectionId = null;
            e.ModifiedAt = DateTime.UtcNow;
        }

        var batches = await db.HypothesisBatches.Where(b => b.AIConnectionId == id).ToListAsync(cancellationToken);
        foreach (var b in batches)
            b.AIConnectionId = null;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyAiSnapshotFromExperiment(HypothesisBatch batch, Experiment experiment)
    {
        batch.AIConnectionId = experiment.AIConnectionId;
        if (experiment.AIConnection != null)
        {
            batch.AiProviderUsed = experiment.AIConnection.Provider;
            batch.AiModelUsed = experiment.AIConnection.Model;
        }
        else
        {
            batch.AiProviderUsed = null;
            batch.AiModelUsed = null;
        }
    }

    private async Task SyncRunQueueForBatchAsync(
        HypothesisBatchId batchId,
        HypothesisBatchState state,
        CancellationToken cancellationToken)
    {
        var existing = await db.RunQueue.Where(q => q.HypothesisBatchId == batchId).ToListAsync(cancellationToken);
        if (state == HypothesisBatchState.Queued)
        {
            if (existing.Count == 0)
                db.RunQueue.Add(new RunQueue { HypothesisBatchId = batchId });
            else if (existing.Count > 1)
                db.RunQueue.RemoveRange(existing.Skip(1).ToList());
        }
        else if (existing.Count > 0)
            db.RunQueue.RemoveRange(existing);
    }

    private async Task RemoveRunQueueEntriesForBatchAsync(HypothesisBatchId batchId, CancellationToken cancellationToken)
    {
        var existing = await db.RunQueue.Where(q => q.HypothesisBatchId == batchId).ToListAsync(cancellationToken);
        if (existing.Count > 0)
            db.RunQueue.RemoveRange(existing);
    }
}
