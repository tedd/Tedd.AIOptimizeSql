using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Polls for queued <see cref="DatabaseAnalysis"/> rows and hands them to
/// <see cref="DatabaseAnalysisService"/>. Claiming is atomic (conditional
/// UPDATE from Queued), so multiple workers cannot process the same analysis.
/// </summary>
public sealed class AnalysisMonitorService(
    IServiceScopeFactory scopeFactory,
    DatabaseAnalysisService analysisService,
    IOptions<OptimizeEngineSettings> settings,
    ILogger<AnalysisMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AnalysisMonitorService started, polling every {Interval}s",
            settings.Value.AnalysisPollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var analysisId = await TryClaimQueuedAnalysisAsync(stoppingToken);
                if (analysisId is not null)
                {
                    logger.LogInformation("Claimed database analysis {AnalysisId}, starting processing", analysisId);
                    await analysisService.RunAnalysisAsync(analysisId.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during analysis poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.Value.AnalysisPollIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("AnalysisMonitorService stopped");
    }

    /// <summary>
    /// Atomically claims the oldest queued analysis by flipping its state from
    /// Queued to Running. Returns null when nothing is queued or another worker won.
    /// </summary>
    private async Task<DatabaseAnalysisId?> TryClaimQueuedAnalysisAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();

        var candidate = await db.DatabaseAnalyses
            .AsNoTracking()
            .Where(a => a.State == DatabaseAnalysisState.Queued)
            .OrderBy(a => a.CreatedAt)
            .Select(a => (DatabaseAnalysisId?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
            return null;

        var now = DateTime.UtcNow;
        var claimed = await db.DatabaseAnalyses
            .Where(a => a.Id == candidate.Value && a.State == DatabaseAnalysisState.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, DatabaseAnalysisState.Running)
                .SetProperty(a => a.LastMessage, "Picked up by worker")
                .SetProperty(a => a.ModifiedAt, now), ct);

        if (claimed == 0)
        {
            logger.LogDebug("Analysis claim race: {AnalysisId} was already claimed", candidate);
            return null;
        }

        return candidate;
    }
}
