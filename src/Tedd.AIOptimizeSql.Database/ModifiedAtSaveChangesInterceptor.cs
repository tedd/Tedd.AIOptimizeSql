using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.Database;

/// <summary>
/// Sets <see cref="ModifiedAt"/> on tracked entities before save. Does not run for <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>.
/// </summary>
public sealed class ModifiedAtSaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly ModifiedAtSaveChangesInterceptor Instance = new();

    private ModifiedAtSaveChangesInterceptor() { }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Stamp(DbContext? context)
    {
        if (context is null) return;
        var now = DateTime.UtcNow;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
                continue;

            switch (entry.Entity)
            {
                case ResearchIteration r:
                    r.ModifiedAt = now;
                    break;
                case Hypothesis h:
                    h.ModifiedAt = now;
                    break;
                case BenchmarkRun b:
                    b.ModifiedAt = now;
                    break;
                case HypothesisLog l:
                    l.ModifiedAt = now;
                    break;
                case RunQueue q:
                    q.ModifiedAt = now;
                    break;
                case Experiment e:
                    e.ModifiedAt = now;
                    break;
                case AIConnection a:
                    a.ModifiedAt = now;
                    break;
                case DatabaseConnection d:
                    d.ModifiedAt = now;
                    break;
                case AiConversation c:
                    c.ModifiedAt = now;
                    break;
            }
        }
    }
}
