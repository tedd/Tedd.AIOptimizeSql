using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class AIOptimizeDataAccessTests
{
    private static AIOptimizeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AIOptimizeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AIOptimizeDbContext(options);
    }

    [Fact]
    public async Task SetHypothesisBatchStateAsync_Queued_adds_single_RunQueue_row()
    {
        await using var db = CreateContext();
        var ai = new AIConnection
        {
            Name = "conn",
            Provider = AiProvider.Ollama,
            Model = "llama",
            Endpoint = "http://127.0.0.1:11434",
            ApiKey = "x",
        };
        db.AIConnections.Add(ai);
        await db.SaveChangesAsync();

        var experiment = new Experiment { Name = "exp", AIConnectionId = ai.Id };
        db.Experiments.Add(experiment);
        await db.SaveChangesAsync();

        var batch = new HypothesisBatch
        {
            ExperimentId = experiment.Id,
            MaxNumberOfHypotheses = 2,
            State = HypothesisBatchState.Stopped,
        };
        db.HypothesisBatches.Add(batch);
        await db.SaveChangesAsync();

        var access = new AIOptimizeDataAccess(db);
        await access.SetHypothesisBatchStateAsync(batch.Id, HypothesisBatchState.Queued);

        Assert.Equal(HypothesisBatchState.Queued, db.HypothesisBatches.Single().State);
        var queue = await db.RunQueue.ToListAsync();
        Assert.Single(queue);
        Assert.Equal(batch.Id, queue[0].HypothesisBatchId);
    }

    [Fact]
    public async Task SetHypothesisBatchStateAsync_non_Queued_removes_RunQueue_rows()
    {
        await using var db = CreateContext();
        var experiment = new Experiment { Name = "exp" };
        db.Experiments.Add(experiment);
        await db.SaveChangesAsync();

        var batch = new HypothesisBatch
        {
            ExperimentId = experiment.Id,
            State = HypothesisBatchState.Queued,
        };
        db.HypothesisBatches.Add(batch);
        await db.SaveChangesAsync();
        db.RunQueue.Add(new RunQueue { HypothesisBatchId = batch.Id });
        await db.SaveChangesAsync();

        var access = new AIOptimizeDataAccess(db);
        await access.SetHypothesisBatchStateAsync(batch.Id, HypothesisBatchState.Stopped);

        Assert.Empty(await db.RunQueue.ToListAsync());
    }

    [Fact]
    public async Task BeginHypothesisBatchRunAsync_sets_running_clears_queue_and_snapshots_ai_from_experiment()
    {
        await using var db = CreateContext();
        var ai = new AIConnection
        {
            Name = "conn",
            Provider = AiProvider.OpenAI,
            Model = "gpt-test",
            Endpoint = "https://api.example.com",
            ApiKey = "secret",
        };
        db.AIConnections.Add(ai);
        await db.SaveChangesAsync();

        var experiment = new Experiment { Name = "exp", AIConnectionId = ai.Id, AIConnection = ai };
        db.Experiments.Add(experiment);
        await db.SaveChangesAsync();

        var batch = new HypothesisBatch
        {
            ExperimentId = experiment.Id,
            State = HypothesisBatchState.Queued,
        };
        db.HypothesisBatches.Add(batch);
        db.RunQueue.Add(new RunQueue { HypothesisBatchId = batch.Id });
        await db.SaveChangesAsync();

        var access = new AIOptimizeDataAccess(db);
        await access.BeginHypothesisBatchRunAsync(batch.Id);

        var reloaded = await db.HypothesisBatches.AsNoTracking().SingleAsync();
        Assert.Equal(HypothesisBatchState.Running, reloaded.State);
        Assert.NotNull(reloaded.StartedAt);
        Assert.Null(reloaded.EndedAt);
        Assert.Equal("Run started", reloaded.LastMessage);
        Assert.Equal(ai.Id, reloaded.AIConnectionId);
        Assert.Equal(AiProvider.OpenAI, reloaded.AiProviderUsed);
        Assert.Equal("gpt-test", reloaded.AiModelUsed);
        Assert.Empty(await db.RunQueue.ToListAsync());
    }
}
