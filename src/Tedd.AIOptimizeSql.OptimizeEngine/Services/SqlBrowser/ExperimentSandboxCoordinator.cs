using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

/// <summary>
/// Provisions and tears down the sandbox a research iteration runs in, and resolves the
/// connection string every stage of the hypothesis lifecycle must use for the experiment.
/// </summary>
public sealed class ExperimentSandboxCoordinator(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ExperimentSandboxCoordinator>();

    /// <summary>
    /// The connection string every stage of the hypothesis lifecycle (schema discovery,
    /// baseline benchmark, apply/benchmark/revert) must use for <paramref name="experiment"/>.
    /// Only <see cref="ExperimentIsolationMode.CloneDatabase"/> changes anything: the initial
    /// catalog is swapped to the sandbox database.
    /// </summary>
    public static string ResolveConnectionString(Experiment experiment)
    {
        var baseConnectionString = experiment.DatabaseConnection?.ConnectionString ?? "";
        if (experiment.IsolationMode != ExperimentIsolationMode.CloneDatabase
            || string.IsNullOrWhiteSpace(experiment.SandboxDatabaseName)
            || string.IsNullOrWhiteSpace(baseConnectionString))
            return baseConnectionString;

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = experiment.SandboxDatabaseName
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// <c>master</c> on the same server as <paramref name="connectionString"/>. CREATE/DROP
    /// DATABASE cannot run inside the database being created or dropped.
    /// </summary>
    private static string MasterConnectionString(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;

    /// <summary>
    /// Runs <see cref="Experiment.SandboxSetupSql"/> once per iteration, before schema
    /// discovery and the baseline benchmark. Idempotent: a second call for an iteration whose
    /// <see cref="ResearchIteration.SandboxProvisioned"/> is already true does nothing, so a
    /// resumed iteration does not provision twice.
    /// </summary>
    public async Task ProvisionAsync(ResearchIteration iteration, Action<string> log, CancellationToken ct)
    {
        var experiment = iteration.Experiment
            ?? throw new InvalidOperationException("Experiment must be loaded.");
        var dbConnection = experiment.DatabaseConnection
            ?? throw new InvalidOperationException("DatabaseConnection must be loaded.");

        if (experiment.IsolationMode == ExperimentIsolationMode.None)
            return;

        if (dbConnection.AnalyzeOnly)
        {
            _logger.LogWarning(
                "Iteration {Id} targets an analyze-only connection; sandbox provisioning skipped, isolation mode ignored.",
                iteration.Id);
            log("Connection is analyze-only (production-safe): sandbox provisioning skipped. The experiment will not be isolated.");
            return;
        }

        if (iteration.SandboxProvisioned)
        {
            log("Sandbox already provisioned for this iteration; skipping setup.");
            return;
        }

        if (string.IsNullOrWhiteSpace(experiment.SandboxSetupSql))
        {
            log($"Isolation mode is {experiment.IsolationMode} but no sandbox setup SQL is configured; skipping provisioning.");
            return;
        }

        var config = new BenchmarkConfig { DatabaseType = "MSSQL" };
        var executor = DatabaseExecutorFactory.Create(config, msg => _logger.LogDebug("{SqlLog}", msg));

        // CREATE DATABASE cannot run inside the database being created.
        var setupConnectionString = experiment.IsolationMode == ExperimentIsolationMode.CloneDatabase
            ? MasterConnectionString(dbConnection.ConnectionString)
            : dbConnection.ConnectionString;

        log($"Provisioning sandbox ({experiment.IsolationMode})...");
        await using (var conn = await executor.OpenConnectionAsync(setupConnectionString, ct))
            executor.ExecuteNonQuery(conn, experiment.SandboxSetupSql);
        log("Sandbox provisioned.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
        var now = DateTime.UtcNow;
        await db.ResearchIterations
            .Where(r => r.Id == iteration.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SandboxProvisioned, true)
                .SetProperty(r => r.ModifiedAt, now), ct);
    }

    /// <summary>
    /// Runs <see cref="Experiment.SandboxTeardownSql"/> for an iteration, regardless of how
    /// the iteration ended. A leaked clone database or sandbox schema is the worst outcome
    /// here, so this must be called from every exit path (success, failure, cancellation).
    /// </summary>
    public async Task TeardownAsync(ResearchIterationId iterationId, Action<string> log, CancellationToken ct)
    {
        Experiment? experiment;
        bool provisioned;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIOptimizeDbContext>();
            var iteration = await db.ResearchIterations
                .Include(r => r.Experiment!).ThenInclude(e => e.DatabaseConnection)
                .FirstOrDefaultAsync(r => r.Id == iterationId, ct);
            if (iteration?.Experiment is null)
                return;
            experiment = iteration.Experiment;
            provisioned = iteration.SandboxProvisioned;
        }

        if (experiment.IsolationMode == ExperimentIsolationMode.None || !provisioned)
            return;

        var dbConnection = experiment.DatabaseConnection;
        if (dbConnection is null || string.IsNullOrWhiteSpace(dbConnection.ConnectionString))
            return;

        if (string.IsNullOrWhiteSpace(experiment.SandboxTeardownSql))
        {
            _logger.LogError(
                "Iteration {Id} provisioned a {Mode} sandbox but has no teardown SQL; it was NOT removed. " +
                "Sandbox schema: {Schema}, sandbox database: {Database}.",
                iterationId, experiment.IsolationMode, experiment.SandboxSchemaName, experiment.SandboxDatabaseName);
            log($"WARNING: sandbox teardown SQL is not configured. The sandbox for this iteration " +
                $"({(experiment.IsolationMode == ExperimentIsolationMode.CloneDatabase ? experiment.SandboxDatabaseName : experiment.SandboxSchemaName)}) " +
                "was left in place and must be removed manually.");
            return;
        }

        var config = new BenchmarkConfig { DatabaseType = "MSSQL" };
        var executor = DatabaseExecutorFactory.Create(config, msg => _logger.LogDebug("{SqlLog}", msg));
        var teardownConnectionString = experiment.IsolationMode == ExperimentIsolationMode.CloneDatabase
            ? MasterConnectionString(dbConnection.ConnectionString)
            : dbConnection.ConnectionString;

        try
        {
            log($"Tearing down sandbox ({experiment.IsolationMode})...");
            await using (var conn = await executor.OpenConnectionAsync(teardownConnectionString, ct))
                executor.ExecuteNonQuery(conn, experiment.SandboxTeardownSql);
            log("Sandbox removed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Sandbox teardown FAILED for iteration {Id} ({Mode}). Sandbox schema: {Schema}, sandbox database: {Database}. " +
                "It may still be present on the server and require manual cleanup.",
                iterationId, experiment.IsolationMode, experiment.SandboxSchemaName, experiment.SandboxDatabaseName);
            log($"ERROR: sandbox teardown failed: {ex.Message}. " +
                $"The sandbox ({(experiment.IsolationMode == ExperimentIsolationMode.CloneDatabase ? experiment.SandboxDatabaseName : experiment.SandboxSchemaName)}) " +
                "may still exist on the server and require manual cleanup.");
        }
    }
}
