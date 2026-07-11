using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database.DataAccess;

public sealed class AIOptimizeDataAccess(IDbContextFactory<AIOptimizeDbContext> dbFactory) : IAIOptimizeDataAccess
{
    public async Task<(IReadOnlyList<ResearchIterationListRow> Items, int TotalCount)> GetResearchIterationsPageAsync(
        int skip,
        int take,
        string? sortLabel,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
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

    public async Task<ResearchIteration?> GetResearchIterationForEditAsync(ResearchIterationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ResearchIterations
            .AsNoTracking()
            .Include(b => b.Experiment)
            .Include(b => b.AIConnection)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<ResearchIterationId> CreateResearchIterationAsync(
        ExperimentId experimentId,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var iteration = new ResearchIteration
        {
            Id = ResearchIterationId.Transient,
            ExperimentId = experimentId,
            Hints = hints,
            MaxNumberOfHypotheses = maxNumberOfHypotheses,
            State = ResearchIterationState.Stopped,
            CreatedAt = DateTime.UtcNow
        };
        db.ResearchIterations.Add(iteration);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return iteration.Id;
    }

    public async Task UpdateResearchIterationEditableFieldsAsync(
        ResearchIterationId id,
        string? hints,
        int maxNumberOfHypotheses,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            var n = await db.ResearchIterations
                .Where(b => b.Id == id)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(b => b.Hints, hints)
                        .SetProperty(b => b.MaxNumberOfHypotheses, maxNumberOfHypotheses)
                        .SetProperty(b => b.ModifiedAt, now),
                    cancellationToken);
            if (n != 1)
                throw new InvalidOperationException($"Research iteration {id} was not found.");
            return;
        }

        var iteration = await db.ResearchIterations.AsTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Research iteration {id} was not found.");
        iteration.Hints = hints;
        iteration.MaxNumberOfHypotheses = maxNumberOfHypotheses;
        iteration.ModifiedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetResearchIterationStateAsync(
        ResearchIterationId id,
        ResearchIterationState state,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ResearchIterations.AnyAsync(b => b.Id == id, cancellationToken))
            throw new InvalidOperationException($"Research iteration {id} was not found.");

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await DeleteRunQueueRowsForIterationAsync(db, id, cancellationToken);

            if (state == ResearchIterationState.Queued)
                db.RunQueue.Add(new RunQueue { ResearchIterationId = id, CreatedAt = now, ModifiedAt = now });

            if (db.Database.IsRelational())
            {
                var n = await db.ResearchIterations
                    .Where(b => b.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.State, state)
                        .SetProperty(b => b.ModifiedAt, now), cancellationToken);
                if (n != 1)
                    throw new InvalidOperationException($"Research iteration {id} was not found.");

                if (state == ResearchIterationState.Queued)
                    await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var iteration = await db.ResearchIterations.AsTracking()
                    .FirstAsync(b => b.Id == id, cancellationToken);
                iteration.State = state;
                iteration.ModifiedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task BeginResearchIterationRunAsync(ResearchIterationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (db.Database.IsRelational())
        {
            var row = await db.ResearchIterations
                .AsNoTracking()
                .Include(b => b.Experiment!)
                .ThenInclude(e => e!.AIConnection)
                .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Research iteration {id} was not found.");

            var experiment = row.Experiment!;
            AIConnectionId? aiConnId = experiment.AIConnectionId;
            AiProvider? aiProv = experiment.AIConnection?.Provider;
            string? aiModel = experiment.AIConnection?.Model;

            var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await RemoveRunQueueEntriesForIterationAsync(db, id, cancellationToken);

                await db.ResearchIterations
                    .Where(b => b.Id == id)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(b => b.AIConnectionId, aiConnId)
                            .SetProperty(b => b.AiProviderUsed, aiProv)
                            .SetProperty(b => b.AiModelUsed, aiModel)
                            .SetProperty(b => b.State, ResearchIterationState.Running)
                            .SetProperty(b => b.StartedAt, now)
                            .SetProperty(b => b.EndedAt, (DateTime?)null)
                            .SetProperty(b => b.LastMessage, "Run started")
                            .SetProperty(b => b.ModifiedAt, now),
                        cancellationToken);

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return;
        }

        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var iteration = await db.ResearchIterations.AsTracking()
                    .Include(b => b.Experiment!)
                    .ThenInclude(e => e!.AIConnection)
                    .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                    ?? throw new InvalidOperationException($"Research iteration {id} was not found.");

                await RemoveRunQueueEntriesForIterationAsync(db, id, cancellationToken);

                ApplyAiSnapshotFromExperiment(iteration, iteration.Experiment!);
                iteration.State = ResearchIterationState.Running;
                iteration.StartedAt = DateTime.UtcNow;
                iteration.EndedAt = null;
                iteration.LastMessage = "Run started";

                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }

    public async Task DeleteResearchIterationAsync(ResearchIterationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.Database.IsRelational())
        {
            await db.ResearchIterations.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var iteration = await db.ResearchIterations.AsTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (iteration is null)
            return;
        db.ResearchIterations.Remove(iteration);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAiConnectionReferencesAsync(AIConnectionId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            await db.Experiments
                .Where(e => e.AIConnectionId == id)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(e => e.AIConnectionId, (AIConnectionId?)null)
                        .SetProperty(e => e.ModifiedAt, now),
                    cancellationToken);

            await db.ResearchIterations
                .Where(b => b.AIConnectionId == id)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(b => b.AIConnectionId, (AIConnectionId?)null)
                        .SetProperty(b => b.ModifiedAt, now),
                    cancellationToken);
            return;
        }

        var experiments = await db.Experiments.AsTracking().Where(e => e.AIConnectionId == id).ToListAsync(cancellationToken);
        foreach (var e in experiments)
        {
            e.AIConnectionId = null;
            e.ModifiedAt = now;
        }

        var iterations = await db.ResearchIterations.AsTracking().Where(b => b.AIConnectionId == id).ToListAsync(cancellationToken);
        foreach (var b in iterations)
        {
            b.AIConnectionId = null;
            b.ModifiedAt = now;
        }

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

    private static Task RemoveRunQueueEntriesForIterationAsync(AIOptimizeDbContext db, ResearchIterationId iterationId, CancellationToken cancellationToken) =>
        DeleteRunQueueRowsForIterationAsync(db, iterationId, cancellationToken);

    private static async Task DeleteRunQueueRowsForIterationAsync(AIOptimizeDbContext db, ResearchIterationId iterationId, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.RunQueue.Where(q => q.ResearchIterationId == iterationId).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        // EF InMemory: ExecuteDeleteAsync is not supported — delete by key via attach/remove.
        var ids = await db.RunQueue.AsNoTracking()
            .Where(q => q.ResearchIterationId == iterationId)
            .Select(q => q.Id)
            .ToListAsync(cancellationToken);
        foreach (var qid in ids)
        {
            var stub = new RunQueue { Id = qid, ResearchIterationId = iterationId };
            db.RunQueue.Attach(stub);
            db.RunQueue.Remove(stub);
        }

        if (ids.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DateTime?> GetMaxAiConnectionModifiedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.AIConnections.AnyAsync(cancellationToken))
            return null;
        return await db.AIConnections.MaxAsync(a => a.ModifiedAt, cancellationToken);
    }

    public async Task<DateTime?> GetMaxDatabaseConnectionModifiedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DatabaseConnections.AnyAsync(cancellationToken))
            return null;
        return await db.DatabaseConnections.MaxAsync(c => c.ModifiedAt, cancellationToken);
    }

    public async Task<DateTime?> GetMaxExperimentModifiedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Experiments.AnyAsync(cancellationToken))
            return null;
        return await db.Experiments.MaxAsync(e => e.ModifiedAt, cancellationToken);
    }

    public async Task<DateTime?> GetMaxDatabaseAnalysisModifiedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DatabaseAnalyses.AnyAsync(cancellationToken))
            return null;
        return await db.DatabaseAnalyses.MaxAsync(a => a.ModifiedAt, cancellationToken);
    }

    public async Task<DateTime?> GetDatabaseAnalysisModifiedAtAsync(DatabaseAnalysisId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DatabaseAnalyses.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => (DateTime?)a.ModifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DateTime?> GetExperimentResultsWatermarkAsync(
        ExperimentId experimentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var expMod = await db.Experiments.AsNoTracking()
            .Where(e => e.Id == experimentId)
            .Select(e => (DateTime?)e.ModifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (expMod is null)
            return null;

        if (!await db.ResearchIterations.AnyAsync(r => r.ExperimentId == experimentId, cancellationToken))
            return expMod;

        var iterMax = await db.ResearchIterations
            .Where(r => r.ExperimentId == experimentId)
            .MaxAsync(r => r.ModifiedAt, cancellationToken);
        return expMod.Value >= iterMax ? expMod.Value : iterMax;
    }

    public async Task<DateTime?> GetResearchIterationsScopeWatermarkAsync(
        ExperimentId? experimentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.ResearchIterations.AsNoTracking();
        if (experimentId.HasValue)
            q = q.Where(r => r.ExperimentId == experimentId.Value);
        if (!await q.AnyAsync(cancellationToken))
            return null;
        return await q.MaxAsync(r => r.ModifiedAt, cancellationToken);
    }

    public async Task<DateTime?> GetResearchIterationModifiedAtAsync(
        ResearchIterationId id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ResearchIterations.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => (DateTime?)r.ModifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DateTime?> GetBenchmarkRunModifiedAtAsync(BenchmarkRunId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.BenchmarkRuns.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => (DateTime?)b.ModifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DateTime?> GetHypothesisDetailWatermarkAsync(HypothesisId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hMod = await db.Hypotheses.AsNoTracking()
            .Where(h => h.Id == id)
            .Select(h => (DateTime?)h.ModifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (hMod is null)
            return null;

        if (!await db.HypothesisLogs.AnyAsync(l => l.HypothesisId == id, cancellationToken))
            return hMod;

        var logMax = await db.HypothesisLogs
            .Where(l => l.HypothesisId == id)
            .MaxAsync(l => l.ModifiedAt, cancellationToken);
        return hMod.Value >= logMax ? hMod.Value : logMax;
    }
}
