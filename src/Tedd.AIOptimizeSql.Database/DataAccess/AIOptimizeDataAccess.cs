using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public sealed class AIOptimizeDataAccess(AIOptimizeDbContext db) : IAIOptimizeDataAccess
{
    public async Task<(IReadOnlyList<ResearchIterationListRow> Items, int TotalCount)> GetResearchIterationsPageAsync(
        int skip,
        int take,
        string? sortLabel,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken = default)
    {
        var query = db.ResearchIterations.AsNoTracking();

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
            .Select(b => new ResearchIterationListRow(
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

    public Task<ResearchIteration?> GetResearchIterationForEditAsync(ResearchIterationId id, CancellationToken cancellationToken = default) =>
        db.ResearchIterations
            .Include(b => b.Experiment)
            .Include(b => b.AIConnection)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<ResearchIterationId> CreateResearchIterationAsync(
        ExperimentId experimentId,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        var iteration = new ResearchIteration
        {
            ExperimentId = experimentId,
            Hints = hints,
            MaxNumberOfHypotheses = maxNumberOfHypotheses,
            State = ResearchIterationState.Stopped,
            CreatedAt = DateTime.UtcNow
        };
        db.ResearchIterations.Add(iteration);
        await db.SaveChangesAsync(cancellationToken);
        return iteration.Id;
    }

    public async Task UpdateResearchIterationEditableFieldsAsync(
        ResearchIterationId id,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        var iteration = await db.ResearchIterations.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                    ?? throw new InvalidOperationException($"Research iteration {id} was not found.");
        iteration.Hints = hints;
        iteration.MaxNumberOfHypotheses = maxNumberOfHypotheses;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetResearchIterationStateAsync(
        ResearchIterationId id,
        ResearchIterationState state,
        CancellationToken cancellationToken = default)
    {
        var iteration = await db.ResearchIterations.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                    ?? throw new InvalidOperationException($"Research iteration {id} was not found.");
        iteration.State = state;
        await SyncRunQueueForIterationAsync(id, state, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task BeginResearchIterationRunAsync(ResearchIterationId id, CancellationToken cancellationToken = default)
    {
        var iteration = await db.ResearchIterations
                       .Include(b => b.Experiment!)
                       .ThenInclude(e => e.AIConnection)
                       .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                   ?? throw new InvalidOperationException($"Research iteration {id} was not found.");

        ApplyAiSnapshotFromExperiment(iteration, iteration.Experiment!);
        iteration.State = ResearchIterationState.Running;
        iteration.StartedAt = DateTime.UtcNow;
        iteration.EndedAt = null;
        iteration.LastMessage = "Run started";

        await RemoveRunQueueEntriesForIterationAsync(id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteResearchIterationAsync(ResearchIterationId id, CancellationToken cancellationToken = default)
    {
        var iteration = await db.ResearchIterations.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (iteration is null)
            return;
        db.ResearchIterations.Remove(iteration);
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

        var iterations = await db.ResearchIterations.Where(b => b.AIConnectionId == id).ToListAsync(cancellationToken);
        foreach (var b in iterations)
            b.AIConnectionId = null;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyAiSnapshotFromExperiment(ResearchIteration iteration, Experiment experiment)
    {
        iteration.AIConnectionId = experiment.AIConnectionId;
        if (experiment.AIConnection != null)
        {
            iteration.AiProviderUsed = experiment.AIConnection.Provider;
            iteration.AiModelUsed = experiment.AIConnection.Model;
        }
        else
        {
            iteration.AiProviderUsed = null;
            iteration.AiModelUsed = null;
        }
    }

    private async Task SyncRunQueueForIterationAsync(
        ResearchIterationId iterationId,
        ResearchIterationState state,
        CancellationToken cancellationToken)
    {
        var existing = await db.RunQueue.Where(q => q.ResearchIterationId == iterationId).ToListAsync(cancellationToken);
        if (state == ResearchIterationState.Queued)
        {
            if (existing.Count == 0)
                db.RunQueue.Add(new RunQueue { ResearchIterationId = iterationId });
            else if (existing.Count > 1)
                db.RunQueue.RemoveRange(existing.Skip(1).ToList());
        }
        else if (existing.Count > 0)
            db.RunQueue.RemoveRange(existing);
    }

    private async Task RemoveRunQueueEntriesForIterationAsync(ResearchIterationId iterationId, CancellationToken cancellationToken)
    {
        var existing = await db.RunQueue.Where(q => q.ResearchIterationId == iterationId).ToListAsync(cancellationToken);
        if (existing.Count > 0)
            db.RunQueue.RemoveRange(existing);
    }
}
