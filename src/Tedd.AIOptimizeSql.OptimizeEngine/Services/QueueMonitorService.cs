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

    /// <summary>
    /// How many times in a row the same iteration may fail to start before it is taken out of
    /// the queue and marked stopped, so one broken item cannot block the queue head forever.
    /// </summary>
    private const int MaxClaimAttemptsPerIteration = 3;

    private ResearchIterationId? _lastFailedIterationId;
    private int _claimFailureCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QueueMonitorService started, polling every {Interval}s",
            settings.Value.QueuePollIntervalSeconds);

        var transientFailureCount = 0;
        var migrationsPendingLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Running against a schema older than this build makes every iteration fail in
                // a way that looks like "queued runs never start", so say so plainly instead.
                var pending = await GetPendingMigrationsAsync(stoppingToken);
                if (pending.Count > 0)
                {
                    if (!migrationsPendingLogged)
                    {
                        migrationsPendingLogged = true;
                        logger.LogError(
                            "Database schema is out of date: {Count} migration(s) pending ({Migrations}). " +
                            "The run queue is paused until they are applied (Database > Migration in the web UI).",
                            pending.Count, string.Join(", ", pending));
                    }
                }
                else
                {
                    if (migrationsPendingLogged)
                    {
                        migrationsPendingLogged = false;
                        logger.LogInformation("Database schema is up to date; resuming run queue polling");
                    }

                    var iterationId = await ClaimNextIterationAsync(stoppingToken);
                    if (iterationId is not null)
                    {
                        logger.LogInformation("Dequeued research iteration {IterationId}, starting processing", iterationId);
                        await iterationEngine.ProcessIterationAsync(iterationId.Value, stoppingToken);
                    }
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
    /// Takes the head of the <see cref="RunQueue"/> and marks it running in one transaction.
    /// A failure leaves the queue row in place so the item is retried; after
    /// <see cref="MaxClaimAttemptsPerIteration"/> consecutive failures the iteration is
    /// stopped with the error recorded on it, so it cannot block the queue head.
    /// </summary>
    private async Task<ResearchIterationId?> ClaimNextIterationAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dataAccess = scope.ServiceProvider.GetRequiredService<IAIOptimizeDataAccess>();

        try
        {
            var iterationId = await dataAccess.TryClaimQueuedResearchIterationAsync(ct);
            _lastFailedIterationId = null;
            _claimFailureCount = 0;
            return iterationId;
        }
        catch (ResearchIterationClaimException ex)
        {
            _claimFailureCount = _lastFailedIterationId == ex.IterationId ? _claimFailureCount + 1 : 1;
            _lastFailedIterationId = ex.IterationId;

            var reason = ex.InnerException?.Message ?? ex.Message;

            if (_claimFailureCount < MaxClaimAttemptsPerIteration)
            {
                logger.LogWarning(ex,
                    "Could not start queued research iteration {IterationId} (attempt {Attempt} of {Max}); it stays queued and will be retried",
                    ex.IterationId, _claimFailureCount, MaxClaimAttemptsPerIteration);
                return null;
            }

            logger.LogError(ex,
                "Giving up on queued research iteration {IterationId} after {Attempts} attempts; marking it stopped",
                ex.IterationId, _claimFailureCount);

            _lastFailedIterationId = null;
            _claimFailureCount = 0;

            try
            {
                await dataAccess.FailQueuedResearchIterationAsync(
                    ex.IterationId, $"Could not start run: {reason}", ct);
            }
            catch (Exception failEx) when (failEx is not OperationCanceledException)
            {
                logger.LogError(failEx,
                    "Failed to record the start failure on research iteration {IterationId}", ex.IterationId);
            }

            return null;
        }
    }

    /// <summary>
    /// Pending migrations mean the running code expects columns the database does not have,
    /// which makes every run fail on its first query. Empty on providers without migrations.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        if (!db.Database.IsRelational())
            return [];

        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }
}
