using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.Database;

/// <summary>
/// Helpers for <see cref="ModifiedAt"/> when using bulk <c>ExecuteUpdateAsync</c>, which bypasses <see cref="ModifiedAtSaveChangesInterceptor"/>.
/// </summary>
public static class ModifiedAtStamping
{
    public static async Task TouchResearchIterationAsync(
        AIOptimizeDbContext db,
        ResearchIterationId iterationId,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            var row = await db.ResearchIterations.AsTracking()
                .FirstOrDefaultAsync(r => r.Id == iterationId, cancellationToken);
            if (row is null) return;
            row.ModifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        await db.ResearchIterations
            .Where(r => r.Id == iterationId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ModifiedAt, now), cancellationToken);
    }

    /// <summary>
    /// After mutating a hypothesis row (or adding logs), bump the hypothesis and parent iteration timestamps for UI polling.
    /// </summary>
    public static async Task StampHypothesisAndParentIterationAsync(
        AIOptimizeDbContext db,
        HypothesisId hypothesisId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var iterationId = await db.Hypotheses.AsNoTracking()
            .Where(h => h.Id == hypothesisId)
            .Select(h => h.ResearchIterationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (iterationId == default)
            return;

        if (db.Database.IsRelational())
        {
            await db.Hypotheses
                .Where(h => h.Id == hypothesisId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ModifiedAt, now), cancellationToken);
            await db.ResearchIterations
                .Where(r => r.Id == iterationId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ModifiedAt, now), cancellationToken);
            return;
        }

        var h = await db.Hypotheses.AsTracking().FirstOrDefaultAsync(x => x.Id == hypothesisId, cancellationToken);
        if (h is not null)
            h.ModifiedAt = now;
        var r = await db.ResearchIterations.AsTracking().FirstOrDefaultAsync(x => x.Id == iterationId, cancellationToken);
        if (r is not null)
            r.ModifiedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Bumps only the parent research iteration (hypothesis row already updated elsewhere).
    /// </summary>
    public static async Task TouchResearchIterationForHypothesisAsync(
        AIOptimizeDbContext db,
        HypothesisId hypothesisId,
        CancellationToken cancellationToken = default)
    {
        var iterationId = await db.Hypotheses.AsNoTracking()
            .Where(h => h.Id == hypothesisId)
            .Select(h => h.ResearchIterationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (iterationId != default)
            await TouchResearchIterationAsync(db, iterationId, cancellationToken);
    }
}
