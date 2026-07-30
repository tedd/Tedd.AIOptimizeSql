using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

/// <summary>
/// The relational code path of <see cref="AIOptimizeDataAccess"/> differs from the
/// InMemory one (ExecuteUpdate/ExecuteDelete, explicit transactions), so the queue
/// round-trip is exercised against a real SQLite database here.
/// </summary>
public class QueueRoundTripSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AIOptimizeDbContext> _options;

    public QueueRoundTripSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AIOptimizeDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AIOptimizeDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestDbContextFactory(DbContextOptions<AIOptimizeDbContext> options)
        : IDbContextFactory<AIOptimizeDbContext>
    {
        public AIOptimizeDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Queueing_an_iteration_makes_it_dequeueable()
    {
        ExperimentId experimentId;
        await using (var db = new AIOptimizeDbContext(_options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            experimentId = experiment.Id;
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        var iterationId = await access.CreateResearchIterationAsync(experimentId, hints: null, maxNumberOfHypotheses: 3);

        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Queued);

        await using var verify = new AIOptimizeDbContext(_options);
        Assert.Equal(ResearchIterationState.Queued,
            verify.ResearchIterations.AsNoTracking().Single().State);
        var queue = await verify.RunQueue.AsNoTracking().ToListAsync();
        Assert.Single(queue);
        Assert.Equal(iterationId, queue[0].ResearchIterationId);
    }

    [Fact]
    public async Task Claiming_the_queue_head_starts_it_and_removes_the_row()
    {
        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        var iterationId = await QueueOneIterationAsync(access);

        var claimed = await access.TryClaimQueuedResearchIterationAsync();

        Assert.Equal(iterationId, claimed);
        await using var verify = new AIOptimizeDbContext(_options);
        var iteration = verify.ResearchIterations.AsNoTracking().Single();
        Assert.Equal(ResearchIterationState.Running, iteration.State);
        Assert.NotNull(iteration.StartedAt);
        Assert.Empty(await verify.RunQueue.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Claiming_an_empty_queue_returns_null()
    {
        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        Assert.Null(await access.TryClaimQueuedResearchIterationAsync());
    }

    [Fact]
    public async Task A_failed_claim_leaves_the_iteration_queued_for_retry()
    {
        // An iteration whose experiment row is missing fails while starting. Without an
        // atomic claim the queue row would already be gone and the iteration would sit in
        // Queued forever with nothing left to retry it.
        var iterationId = InsertOrphanedQueuedIteration();

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        var ex = await Assert.ThrowsAsync<ResearchIterationClaimException>(
            () => access.TryClaimQueuedResearchIterationAsync());
        Assert.Equal(iterationId, ex.IterationId);

        await using var verify = new AIOptimizeDbContext(_options);
        Assert.Single(await verify.RunQueue.AsNoTracking().ToListAsync());
        Assert.Equal(ResearchIterationState.Queued,
            verify.ResearchIterations.AsNoTracking().Single(r => r.Id == iterationId).State);
    }

    [Fact]
    public async Task Failing_a_queued_iteration_stops_it_and_clears_the_queue()
    {
        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        var iterationId = await QueueOneIterationAsync(access);

        await access.FailQueuedResearchIterationAsync(iterationId, "Could not start run: boom");

        await using var verify = new AIOptimizeDbContext(_options);
        var iteration = verify.ResearchIterations.AsNoTracking().Single();
        Assert.Equal(ResearchIterationState.Stopped, iteration.State);
        Assert.Equal("Could not start run: boom", iteration.LastMessage);
        Assert.Empty(await verify.RunQueue.AsNoTracking().ToListAsync());
    }

    private async Task<ResearchIterationId> QueueOneIterationAsync(AIOptimizeDataAccess access)
    {
        ExperimentId experimentId;
        await using (var db = new AIOptimizeDbContext(_options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            experimentId = experiment.Id;
        }

        var iterationId = await access.CreateResearchIterationAsync(experimentId, hints: null, maxNumberOfHypotheses: 3);
        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Queued);
        return iterationId;
    }

    /// <summary>
    /// Writes a queued iteration pointing at an experiment that does not exist, with SQLite's
    /// foreign keys temporarily off so the broken state can be created on purpose.
    /// </summary>
    private ResearchIterationId InsertOrphanedQueuedIteration()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO ResearchIterations (ExperimentId, MaxNumberOfHypotheses, State, SandboxProvisioned, CreatedAt, ModifiedAt)
            VALUES (99999, 3, 1, 0, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO RunQueue (ResearchIterationId, CreatedAt, ModifiedAt)
            VALUES (last_insert_rowid(), '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            SELECT ResearchIterationId FROM RunQueue;
            """;
        return (ResearchIterationId)Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task Requeueing_an_already_queued_iteration_keeps_one_row()
    {
        ExperimentId experimentId;
        await using (var db = new AIOptimizeDbContext(_options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            experimentId = experiment.Id;
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(_options));
        var iterationId = await access.CreateResearchIterationAsync(experimentId, hints: null, maxNumberOfHypotheses: 3);

        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Queued);
        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Queued);

        await using var verify = new AIOptimizeDbContext(_options);
        Assert.Single(await verify.RunQueue.AsNoTracking().ToListAsync());
    }
}
