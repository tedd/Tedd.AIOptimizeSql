using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Live feedback channel for a running research iteration: appends activity lines to
/// <see cref="ResearchIterationLog"/> and updates the iteration's <c>LastMessage</c> so the
/// UI can follow long-running phases (schema discovery, baseline benchmark, hypothesis
/// testing) while they run. All methods swallow their own errors — feedback must never
/// break a run.
/// </summary>
public sealed class ResearchIterationLogger(
    IServiceScopeFactory scopeFactory,
    ILogger<ResearchIterationLogger> logger)
{
    private const int MaxLogMessageChars = 48_000;

    /// <summary>Appends an activity line to the iteration's log and bumps its ModifiedAt watermark.</summary>
    public async Task AppendAsync(
        ResearchIterationId iterationId,
        string message,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;
            db.ResearchIterationLogs.Add(new ResearchIterationLog
            {
                Id = ResearchIterationLogId.Transient,
                ResearchIterationId = iterationId,
                Message = Truncate(message),
                Source = source,
                CreatedAt = now,
                ModifiedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await ModifiedAtStamping.TouchResearchIterationAsync(db, iterationId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append research iteration log for {IterationId}", iterationId);
        }
    }

    /// <summary>Sets the iteration's LastMessage (the short status line shown in the UI).</summary>
    public async Task SetMessageAsync(
        ResearchIterationId iterationId,
        string message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var now = DateTime.UtcNow;

            if (db.Database.IsRelational())
            {
                await db.ResearchIterations
                    .Where(b => b.Id == iterationId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.LastMessage, message)
                        .SetProperty(b => b.ModifiedAt, now), cancellationToken);
                return;
            }

            var row = await db.ResearchIterations.AsTracking()
                .FirstOrDefaultAsync(b => b.Id == iterationId, cancellationToken);
            if (row is null)
                return;
            row.LastMessage = message;
            row.ModifiedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update research iteration {IterationId} message", iterationId);
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxLogMessageChars ? message : message[..MaxLogMessageChars] + "\n… (truncated)";
}
