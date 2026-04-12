using Microsoft.EntityFrameworkCore;

using System.Text.Json;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database;

public class AIOptimizeDbContext : DbContext
{
    public AIOptimizeDbContext(DbContextOptions<AIOptimizeDbContext> options) : base(options) { }

    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();
    public DbSet<AIConnection> AIConnections => Set<AIConnection>();
    public DbSet<Experiment> Experiments => Set<Experiment>();
    public DbSet<HypothesisBatch> HypothesisBatches => Set<HypothesisBatch>();
    public DbSet<Hypothesis> Hypotheses => Set<Hypothesis>();
    public DbSet<BenchmarkRun> BenchmarkRuns => Set<BenchmarkRun>();
    public DbSet<RunQueue> RunQueue => Set<RunQueue>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DatabaseConnectionId>().HaveConversion<int>();
        builder.Properties<AIConnectionId>().HaveConversion<int>();
        builder.Properties<ExperimentId>().HaveConversion<int>();
        builder.Properties<HypothesisBatchId>().HaveConversion<int>();
        builder.Properties<HypothesisId>().HaveConversion<int>();
        builder.Properties<BenchmarkRunId>().HaveConversion<int>();
        builder.Properties<RunQueueId>().HaveConversion<int>();

        // AiProvider enum stored as string in DB
        builder.Properties<AiProvider>().HaveConversion<string>().HaveMaxLength(128);
        builder.Properties<HypothesisBatchState>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<HypothesisState>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Experiment>(entity =>
        {
            entity.HasOne(p => p.DatabaseConnection)
                .WithMany()
                .HasForeignKey(p => p.DatabaseConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(p => p.AIConnection)
                .WithMany()
                .HasForeignKey(p => p.AIConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<HypothesisBatch>(entity =>
        {
            entity.HasOne(r => r.Experiment)
                .WithMany(p => p.BatchRuns)
                .HasForeignKey(r => r.ExperimentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.AIConnection)
                .WithMany()
                .HasForeignKey(r => r.AIConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BenchmarkRun>(entity =>
        {
            entity.Property(e => e.ActualPlanXml)
                .HasConversion(
                    v => JsonSerializer.Serialize(v),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>())
                .HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<RunQueue>(entity =>
        {
            entity.HasOne(r => r.HypothesisBatch)
                .WithMany()
                .HasForeignKey(r => r.HypothesisBatchId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.HypothesisBatchId).IsUnique();
        });

        modelBuilder.Entity<Hypothesis>(entity =>
        {
            entity.HasOne(h => h.HypothesisBatch)
                .WithMany(r => r.Hypotheses)
                .HasForeignKey(h => h.HypothesisBatchId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction: avoids SQL Server cycle with CASCADE from HypothesisBatch on the same table.
            entity.HasOne(h => h.BuilOptimizationHypothesis)
                .WithMany()
                .HasForeignKey(h => h.BuildsOnHypothesisId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction: SQL Server rejects multiple cascade/set-null paths when combined with CASCADE from HypothesisBatch.
            entity.HasOne(h => h.BenchmarkRunBefore)
                .WithMany()
                .HasForeignKey(h => h.BenchmarkRunIdBefore)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(h => h.BenchmarkRunAfter)
                .WithMany()
                .HasForeignKey(h => h.BenchmarkRunIdAfter)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
