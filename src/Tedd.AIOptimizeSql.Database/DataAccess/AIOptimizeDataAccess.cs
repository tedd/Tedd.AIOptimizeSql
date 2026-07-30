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
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await RemoveRunQueueEntriesForIterationAsync(db, id, cancellationToken);
            await ApplyRunStartAsync(db, id, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ResearchIterationId?> TryClaimQueuedResearchIterationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var head = await db.RunQueue
            .AsNoTracking()
            .OrderBy(q => q.CreatedAt)
            .ThenBy(q => q.Id)
            .Select(q => new { q.Id, q.ResearchIterationId })
            .FirstOrDefaultAsync(cancellationToken);

        if (head is null)
            return null;

        // Removing the queue row and marking the iteration Running must succeed or fail
        // together. A delete that commits on its own would drop the work item while the
        // iteration stays Queued forever, with nothing left to retry it.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = false;
        try
        {
            claimed = await DeleteRunQueueRowAsync(db, head.Id, cancellationToken);
            if (!claimed)
            {
                await tx.RollbackAsync(cancellationToken);
                return null;
            }

            await ApplyRunStartAsync(db, head.ResearchIterationId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return head.ResearchIterationId;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            if (!claimed || ex is OperationCanceledException)
                throw;

            // The queue row is back after the rollback, so the iteration will be retried.
            // Surface which iteration failed so the caller can report it on the iteration.
            throw new ResearchIterationClaimException(head.ResearchIterationId, ex);
        }
    }

    /// <summary>
    /// Copies the AI snapshot from the parent experiment and moves the iteration into
    /// <see cref="ResearchIterationState.Running"/>. Runs inside the caller's transaction and
    /// does not touch the run queue.
    /// </summary>
    private static async Task ApplyRunStartAsync(
        AIOptimizeDbContext db,
        ResearchIterationId id,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        if (db.Database.IsRelational())
        {
            var row = await db.ResearchIterations
                .AsNoTracking()
                .Include(b => b.Experiment!)
                .ThenInclude(e => e!.AIConnection)
                .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Research iteration {id} was not found.");

            var experiment = row.Experiment
                ?? throw new InvalidOperationException($"Research iteration {id} has no parent experiment.");
            AIConnectionId? aiConnId = experiment.AIConnectionId;
            AiProvider? aiProv = experiment.AIConnection?.Provider;
            string? aiModel = experiment.AIConnection?.Model;

            var n = await db.ResearchIterations
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
            if (n != 1)
                throw new InvalidOperationException($"Research iteration {id} was not found.");

            return;
        }

        var iteration = await db.ResearchIterations.AsTracking()
            .Include(b => b.Experiment!)
            .ThenInclude(e => e!.AIConnection)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Research iteration {id} was not found.");

        ApplyAiSnapshotFromExperiment(iteration, iteration.Experiment
            ?? throw new InvalidOperationException($"Research iteration {id} has no parent experiment."));
        iteration.State = ResearchIterationState.Running;
        iteration.StartedAt = now;
        iteration.EndedAt = null;
        iteration.LastMessage = "Run started";
        iteration.ModifiedAt = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailQueuedResearchIterationAsync(
        ResearchIterationId id,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await RemoveRunQueueEntriesForIterationAsync(db, id, cancellationToken);

            if (db.Database.IsRelational())
            {
                await db.ResearchIterations
                    .Where(b => b.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.State, ResearchIterationState.Stopped)
                        .SetProperty(b => b.EndedAt, now)
                        .SetProperty(b => b.LastMessage, message)
                        .SetProperty(b => b.ModifiedAt, now), cancellationToken);
            }
            else
            {
                var iteration = await db.ResearchIterations.AsTracking()
                    .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                if (iteration is not null)
                {
                    iteration.State = ResearchIterationState.Stopped;
                    iteration.EndedAt = now;
                    iteration.LastMessage = message;
                    iteration.ModifiedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task DeleteResearchIterationAsync(ResearchIterationId id, CancellationToken cancellationToken = default) =>
        DeleteResearchIterationsAsync([id], cancellationToken);

    public async Task<int> DeleteExperimentsAsync(IReadOnlyCollection<ExperimentId> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        int deleted;

        if (db.Database.IsRelational())
        {
            // Clear analysis-finding links explicitly (defense in depth on top of the
            // ON DELETE SET NULL constraint) so the finding survives with a stamped change.
            await db.AnalysisFindings
                .Where(f => f.ProposedExperimentId != null && ids.Contains(f.ProposedExperimentId.Value))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ProposedExperimentId, (ExperimentId?)null)
                    .SetProperty(f => f.ModifiedAt, now), cancellationToken);

            deleted = await db.Experiments.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var findings = await db.AnalysisFindings.AsTracking()
                .Where(f => f.ProposedExperimentId != null && ids.Contains(f.ProposedExperimentId.Value))
                .ToListAsync(cancellationToken);
            foreach (var f in findings)
            {
                f.ProposedExperimentId = null;
                f.ModifiedAt = now;
            }

            var experiments = await db.Experiments.AsTracking()
                .Where(e => ids.Contains(e.Id))
                .ToListAsync(cancellationToken);
            db.Experiments.RemoveRange(experiments);
            await db.SaveChangesAsync(cancellationToken);
            deleted = experiments.Count;
        }

        await DeleteOrphanBenchmarkRunsAsync(db, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteResearchIterationsAsync(IReadOnlyCollection<ResearchIterationId> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        int deleted;

        if (db.Database.IsRelational())
        {
            deleted = await db.ResearchIterations.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var iterations = await db.ResearchIterations.AsTracking()
                .Where(r => ids.Contains(r.Id))
                .ToListAsync(cancellationToken);
            db.ResearchIterations.RemoveRange(iterations);
            await db.SaveChangesAsync(cancellationToken);
            deleted = iterations.Count;
        }

        await DeleteOrphanBenchmarkRunsAsync(db, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteHypothesesAsync(IReadOnlyCollection<HypothesisId> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        int deleted;

        if (db.Database.IsRelational())
        {
            // The self-referencing BuildsOnHypothesisId FK is NO ACTION (SQL Server does not
            // allow cascading actions on self-references), so clear surviving references first.
            await db.Hypotheses
                .Where(h => h.BuildsOnHypothesisId != null && ids.Contains(h.BuildsOnHypothesisId.Value))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.BuildsOnHypothesisId, (HypothesisId?)null)
                    .SetProperty(h => h.ModifiedAt, now), cancellationToken);

            deleted = await db.Hypotheses.Where(h => ids.Contains(h.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var referencing = await db.Hypotheses.AsTracking()
                .Where(h => h.BuildsOnHypothesisId != null && ids.Contains(h.BuildsOnHypothesisId.Value))
                .ToListAsync(cancellationToken);
            foreach (var h in referencing)
            {
                h.BuildsOnHypothesisId = null;
                h.ModifiedAt = now;
            }

            var hypotheses = await db.Hypotheses.AsTracking()
                .Where(h => ids.Contains(h.Id))
                .ToListAsync(cancellationToken);
            db.Hypotheses.RemoveRange(hypotheses);
            await db.SaveChangesAsync(cancellationToken);
            deleted = hypotheses.Count;
        }

        await DeleteOrphanBenchmarkRunsAsync(db, cancellationToken);
        return deleted;
    }

    /// <summary>
    /// Removes benchmark runs no longer referenced by any iteration baseline or hypothesis
    /// before/after link. Benchmark FKs are NO ACTION, so deletes of their owners leave
    /// orphans behind; this keeps the table from growing forever.
    /// </summary>
    private static async Task DeleteOrphanBenchmarkRunsAsync(AIOptimizeDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.BenchmarkRuns
                .Where(b =>
                    !db.ResearchIterations.Any(r => r.BaselineBenchmarkRunId == b.Id) &&
                    !db.Hypotheses.Any(h => h.BenchmarkRunIdBefore == b.Id || h.BenchmarkRunIdAfter == b.Id))
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var referenced = new HashSet<BenchmarkRunId>();
        foreach (var id in await db.ResearchIterations.AsNoTracking()
                     .Where(r => r.BaselineBenchmarkRunId != null)
                     .Select(r => r.BaselineBenchmarkRunId!.Value).ToListAsync(cancellationToken))
            referenced.Add(id);
        foreach (var h in await db.Hypotheses.AsNoTracking()
                     .Select(h => new { h.BenchmarkRunIdBefore, h.BenchmarkRunIdAfter }).ToListAsync(cancellationToken))
        {
            if (h.BenchmarkRunIdBefore is { } before) referenced.Add(before);
            if (h.BenchmarkRunIdAfter is { } after) referenced.Add(after);
        }

        var orphans = await db.BenchmarkRuns.AsTracking().ToListAsync(cancellationToken);
        orphans.RemoveAll(b => referenced.Contains(b.Id));
        if (orphans.Count > 0)
        {
            db.BenchmarkRuns.RemoveRange(orphans);
            await db.SaveChangesAsync(cancellationToken);
        }
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

    /// <summary>
    /// Deletes a single queue row by key. Returns false when another worker already took it.
    /// </summary>
    private static async Task<bool> DeleteRunQueueRowAsync(AIOptimizeDbContext db, RunQueueId id, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
            return await db.RunQueue.Where(q => q.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

        var row = await db.RunQueue.AsTracking().FirstOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (row is null)
            return false;

        db.RunQueue.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

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
