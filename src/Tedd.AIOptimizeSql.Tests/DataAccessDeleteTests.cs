using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Tests;

/// <summary>
/// Verifies the centralized delete methods handle every dependent relationship:
/// analysis-finding links, self-referencing builds-on links, and orphaned
/// benchmark runs.
/// </summary>
public class DataAccessDeleteTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<AIOptimizeDbContext>
    {
        private readonly DbContextOptions<AIOptimizeDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AIOptimizeDbContext> options) => _options = options;
        public AIOptimizeDbContext CreateDbContext() => new(_options);
    }

    private static DbContextOptions<AIOptimizeDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AIOptimizeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    [Fact]
    public async Task DeleteExperimentsAsync_clears_analysis_finding_links_and_keeps_findings()
    {
        var options = CreateOptions();
        ExperimentId experimentId;
        await using (var db = new AIOptimizeDbContext(options))
        {
            var experiment = new Experiment { Name = "proposed exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            experimentId = experiment.Id;

            var analysis = new DatabaseAnalysis { Id = DatabaseAnalysisId.Transient, Name = "analysis" };
            db.DatabaseAnalyses.Add(analysis);
            await db.SaveChangesAsync();

            db.AnalysisFindings.Add(new AnalysisFinding
            {
                Id = AnalysisFindingId.Transient,
                DatabaseAnalysisId = analysis.Id,
                Title = "finding with experiment link",
                ProposedExperimentId = experimentId,
            });
            await db.SaveChangesAsync();
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        var deleted = await access.DeleteExperimentsAsync([experimentId]);

        Assert.Equal(1, deleted);
        await using var verify = new AIOptimizeDbContext(options);
        Assert.Empty(verify.Experiments);
        var finding = await verify.AnalysisFindings.AsNoTracking().SingleAsync();
        Assert.Null(finding.ProposedExperimentId); // finding survives, link cleared
    }

    [Fact]
    public async Task DeleteHypothesesAsync_clears_builds_on_references_from_survivors()
    {
        var options = CreateOptions();
        HypothesisId firstId, secondId;
        await using (var db = new AIOptimizeDbContext(options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();

            var iteration = new ResearchIteration { Id = ResearchIterationId.Transient, ExperimentId = experiment.Id };
            db.ResearchIterations.Add(iteration);
            await db.SaveChangesAsync();

            var first = new Hypothesis { ResearchIterationId = iteration.Id };
            db.Hypotheses.Add(first);
            await db.SaveChangesAsync();
            firstId = first.Id;

            var second = new Hypothesis { ResearchIterationId = iteration.Id, BuildsOnHypothesisId = firstId };
            db.Hypotheses.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        var deleted = await access.DeleteHypothesesAsync([firstId]);

        Assert.Equal(1, deleted);
        await using var verify = new AIOptimizeDbContext(options);
        var survivor = await verify.Hypotheses.AsNoTracking().SingleAsync();
        Assert.Equal(secondId, survivor.Id);
        Assert.Null(survivor.BuildsOnHypothesisId); // reference cleared, no FK violation
    }

    [Fact]
    public async Task DeleteHypothesesAsync_removes_orphaned_benchmark_runs()
    {
        var options = CreateOptions();
        HypothesisId hypothesisId;
        BenchmarkRunId keptBaselineId;
        await using (var db = new AIOptimizeDbContext(options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();

            var baseline = new BenchmarkRun { TotalTimeMs = 0, TotalServerCpuTimeMs = 0, TotalServerElapsedTimeMs = 0 };
            var before = new BenchmarkRun { TotalTimeMs = 0, TotalServerCpuTimeMs = 0, TotalServerElapsedTimeMs = 0 };
            var after = new BenchmarkRun { TotalTimeMs = 0, TotalServerCpuTimeMs = 0, TotalServerElapsedTimeMs = 0 };
            db.BenchmarkRuns.AddRange(baseline, before, after);
            await db.SaveChangesAsync();
            keptBaselineId = baseline.Id;

            var iteration = new ResearchIteration
            {
                Id = ResearchIterationId.Transient,
                ExperimentId = experiment.Id,
                BaselineBenchmarkRunId = baseline.Id,
            };
            db.ResearchIterations.Add(iteration);
            await db.SaveChangesAsync();

            var hypothesis = new Hypothesis
            {
                ResearchIterationId = iteration.Id,
                BenchmarkRunIdBefore = before.Id,
                BenchmarkRunIdAfter = after.Id,
            };
            db.Hypotheses.Add(hypothesis);
            await db.SaveChangesAsync();
            hypothesisId = hypothesis.Id;
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        await access.DeleteHypothesesAsync([hypothesisId]);

        await using var verify = new AIOptimizeDbContext(options);
        var remainingRuns = await verify.BenchmarkRuns.AsNoTracking().Select(b => b.Id).ToListAsync();
        // before/after runs are orphaned by the delete and cleaned up; the baseline stays.
        Assert.Equal([keptBaselineId], remainingRuns);
    }

    [Fact]
    public async Task DeleteResearchIterationsAsync_removes_iteration_and_orphaned_baseline_run()
    {
        var options = CreateOptions();
        ResearchIterationId iterationId;
        await using (var db = new AIOptimizeDbContext(options))
        {
            var experiment = new Experiment { Name = "exp" };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();

            var baseline = new BenchmarkRun { TotalTimeMs = 0, TotalServerCpuTimeMs = 0, TotalServerElapsedTimeMs = 0 };
            db.BenchmarkRuns.Add(baseline);
            await db.SaveChangesAsync();

            var iteration = new ResearchIteration
            {
                Id = ResearchIterationId.Transient,
                ExperimentId = experiment.Id,
                BaselineBenchmarkRunId = baseline.Id,
            };
            db.ResearchIterations.Add(iteration);
            await db.SaveChangesAsync();
            iterationId = iteration.Id;
        }

        var access = new AIOptimizeDataAccess(new TestDbContextFactory(options));
        var deleted = await access.DeleteResearchIterationsAsync([iterationId]);

        Assert.Equal(1, deleted);
        await using var verify = new AIOptimizeDbContext(options);
        Assert.Empty(verify.ResearchIterations);
        Assert.Empty(verify.BenchmarkRuns); // orphaned baseline cleaned up
    }

    [Fact]
    public async Task DeleteExperimentsAsync_with_empty_ids_is_a_noop()
    {
        var access = new AIOptimizeDataAccess(new TestDbContextFactory(CreateOptions()));
        Assert.Equal(0, await access.DeleteExperimentsAsync([]));
        Assert.Equal(0, await access.DeleteResearchIterationsAsync([]));
        Assert.Equal(0, await access.DeleteHypothesesAsync([]));
    }
}
