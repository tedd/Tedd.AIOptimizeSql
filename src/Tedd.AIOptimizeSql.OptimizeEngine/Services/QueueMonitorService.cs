using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Polls <see cref="RunQueue"/> at a configurable interval and hands dequeued
/// iterations to <see cref="ResearchIterationProcessingEngine"/> for processing.
/// </summary>
public sealed class QueueMonitorService(
    IServiceScopeFactory scopeFactory,
    ResearchIterationProcessingEngine iterationEngine,
    IOptions<OptimizeEngineSettings> settings,
    ILogger<QueueMonitorService> logger) : BackgroundService
{
    private const int BaseTransientBackoffMs = 1_000;
    private const int MaxTransientBackoffMs = 120_000;

    /// <summary>
    /// SQL Server / Azure SQL error numbers commonly treated as transient (connectivity,
    /// throttling, deadlocks, timeouts). See Azure SQL retry documentation.
    /// </summary>
    private static readonly HashSet<int> TransientSqlErrorNumbers =
    [
        -2, // timeout
        2, 53, 64, 121, 233, 10053, 10054, 10060, // connection / transport
        994, 1205, // deadlock
        40197, 40501, 40613, // Azure processing / service busy / not available
        10928, 10929, // resource limits
        49918, 49919, 49920,
        4221, 42108, 8628,
        8645, 8651, 8657, 8662, // query processing memory / workers
        701, // out of memory (often transient under load)
        419, // physical connection broken
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QueueMonitorService started, polling every {Interval}s",
            settings.Value.QueuePollIntervalSeconds);

        var transientFailureCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var iterationId = await TryDequeueAsync(stoppingToken);
                if (iterationId is not null)
                {
                    logger.LogInformation("Dequeued research iteration {IterationId}, starting processing", iterationId);

                    using var scope = scopeFactory.CreateScope();
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IAIOptimizeDataAccess>();
                    await dataAccess.BeginResearchIterationRunAsync(iterationId.Value, stoppingToken);

                    await iterationEngine.ProcessIterationAsync(iterationId.Value, stoppingToken);
                }

                transientFailureCount = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsTransientDatabaseException(ex))
            {
                transientFailureCount++;
                var backoff = ComputeTransientBackoff(transientFailureCount);
                logger.LogWarning(ex,
                    "Transient database error during queue poll cycle (attempt {Attempt}); retrying after {BackoffMs}ms",
                    transientFailureCount, backoff.TotalMilliseconds);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }
            catch (Exception ex)
            {
                transientFailureCount = 0;
                logger.LogError(ex, "Non-transient error during queue poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.Value.QueuePollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("QueueMonitorService stopped");
    }

    private static TimeSpan ComputeTransientBackoff(int failureCount)
    {
        var exponent = Math.Min(Math.Max(failureCount - 1, 0), 20);
        var multiplier = 1L << exponent;
        var ms = BaseTransientBackoffMs * multiplier;
        ms = Math.Min(ms, MaxTransientBackoffMs);
        return TimeSpan.FromMilliseconds(ms);
    }

    private static bool IsTransientDatabaseException(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException:
                    return true;
                case IOException:
                    return true;
                case SocketException:
                    return true;
                case SqlException sql:
                    if (IsTransientSqlException(sql))
                        return true;
                    break;
            }
        }

        return false;
    }

    private static bool IsTransientSqlException(SqlException ex)
    {
        if (TransientSqlErrorNumbers.Contains(ex.Number))
            return true;

        foreach (SqlError err in ex.Errors)
        {
            if (TransientSqlErrorNumbers.Contains(err.Number))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to atomically remove one item from the <see cref="RunQueue"/>.
    /// Returns the <see cref="ResearchIterationId"/> if successful, or null if the
    /// queue is empty or another worker claimed the item (race condition).
    /// </summary>
    private async Task<ResearchIterationId?> TryDequeueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();

        var head = await db.RunQueue
            .AsNoTracking()
            .OrderBy(q => q.CreatedAt)
            .Select(q => new { q.Id, q.ResearchIterationId })
            .FirstOrDefaultAsync(ct);

        if (head is null)
            return null;

        var deleted = await db.RunQueue.Where(q => q.Id == head.Id).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            logger.LogDebug("Queue item race: row {QueueId} was already dequeued", head.Id);
            return null;
        }

        return head.ResearchIterationId;
    }
}
