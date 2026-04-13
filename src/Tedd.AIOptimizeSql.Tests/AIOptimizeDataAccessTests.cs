using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

public class AIOptimizeDataAccessTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<AIOptimizeDbContext>
    {
        private readonly DbContextOptions<AIOptimizeDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AIOptimizeDbContext> options) => _options = options;
        public AIOptimizeDbContext CreateDbContext() => new(_options);
    }

    private static DbContextOptions<AIOptimizeDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<AIOptimizeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    [Fact]
    public async Task SetResearchIterationStateAsync_Queued_adds_single_RunQueue_row()
    {
        var options = CreateOptions();
        await using (var db = new AIOptimizeDbContext(options))
        {
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

            var iteration = new ResearchIteration
            {
                ExperimentId = experiment.Id,
                MaxNumberOfHypotheses = 2,
                State = ResearchIterationState.Stopped,
            };
            db.ResearchIterations.Add(iteration);
            await db.SaveChangesAsync();
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        ResearchIterationId iterationId;
        await using (var readIds = new AIOptimizeDbContext(options))
        {
            iterationId = readIds.ResearchIterations.Single().Id;
        }

        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Queued);

        await using var verify = new AIOptimizeDbContext(options);
        Assert.Equal(ResearchIterationState.Queued, verify.ResearchIterations.AsNoTracking().Single().State);
        var queue = await verify.RunQueue.ToListAsync();
        Assert.Single(queue);
        Assert.Equal(iterationId, queue[0].ResearchIterationId);
    }

    [Fact]
    public async Task SetResearchIterationStateAsync_non_Queued_removes_RunQueue_rows()
    {
        var options = CreateOptions();
        await using (var db = new AIOptimizeDbContext(options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();

            var iteration = new ResearchIteration
            {
                ExperimentId = experiment.Id,
                State = ResearchIterationState.Queued,
            };
            db.ResearchIterations.Add(iteration);
            await db.SaveChangesAsync();
            db.RunQueue.Add(new RunQueue { ResearchIterationId = iteration.Id });
            await db.SaveChangesAsync();
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        ResearchIterationId iterationId;
        await using (var readIds = new AIOptimizeDbContext(options))
            iterationId = readIds.ResearchIterations.Single().Id;

        await access.SetResearchIterationStateAsync(iterationId, ResearchIterationState.Stopped);

        await using var verify = new AIOptimizeDbContext(options);
        Assert.Empty(await verify.RunQueue.ToListAsync());
    }

    [Fact]
    public async Task BeginResearchIterationRunAsync_sets_running_clears_queue_and_snapshots_ai_from_experiment()
    {
        var options = CreateOptions();
        await using (var db = new AIOptimizeDbContext(options))
        {
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

            var iteration = new ResearchIteration
            {
                ExperimentId = experiment.Id,
                State = ResearchIterationState.Queued,
            };
            db.ResearchIterations.Add(iteration);
            db.RunQueue.Add(new RunQueue { ResearchIterationId = iteration.Id });
            await db.SaveChangesAsync();
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        ResearchIterationId iterationId;
        AIConnectionId aiId;
        await using (var readIds = new AIOptimizeDbContext(options))
        {
            iterationId = readIds.ResearchIterations.Single().Id;
            aiId = readIds.AIConnections.Single().Id;
        }

        await access.BeginResearchIterationRunAsync(iterationId);

        await using var verify = new AIOptimizeDbContext(options);
        var reloaded = await verify.ResearchIterations.AsNoTracking().SingleAsync();
        Assert.Equal(ResearchIterationState.Running, reloaded.State);
        Assert.NotNull(reloaded.StartedAt);
        Assert.Null(reloaded.EndedAt);
        Assert.Equal("Run started", reloaded.LastMessage);
        Assert.Equal(aiId, reloaded.AIConnectionId);
        Assert.Equal(AiProvider.OpenAI, reloaded.AiProviderUsed);
        Assert.Equal("gpt-test", reloaded.AiModelUsed);
        Assert.Empty(await verify.RunQueue.ToListAsync());
    }
}
