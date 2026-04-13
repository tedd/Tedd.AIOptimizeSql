using Microsoft.EntityFrameworkCore;

using System.Text.Json;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;

namespace Tedd.AIOptimizeSql.Database;

public class AIOptimizeDbContext : DbContext
{
    public AIOptimizeDbContext(DbContextOptions<AIOptimizeDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();
    public DbSet<AIConnection> AIConnections => Set<AIConnection>();
    public DbSet<Experiment> Experiments => Set<Experiment>();
    public DbSet<ResearchIteration> ResearchIterations => Set<ResearchIteration>();
    public DbSet<Hypothesis> Hypotheses => Set<Hypothesis>();
    public DbSet<HypothesisLog> HypothesisLogs => Set<HypothesisLog>();
    public DbSet<BenchmarkRun> BenchmarkRuns => Set<BenchmarkRun>();
    public DbSet<RunQueue> RunQueue => Set<RunQueue>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DatabaseConnectionId>().HaveConversion<int>();
        builder.Properties<AIConnectionId>().HaveConversion<int>();
        builder.Properties<ExperimentId>().HaveConversion<int>();
        builder.Properties<ResearchIterationId>().HaveConversion<int>();
        builder.Properties<HypothesisId>().HaveConversion<int>();
        builder.Properties<HypothesisLogId>().HaveConversion<int>();
        builder.Properties<BenchmarkRunId>().HaveConversion<int>();
        builder.Properties<RunQueueId>().HaveConversion<int>();

        // AiProvider enum stored as string in DB
        builder.Properties<AiProvider>().HaveConversion<string>().HaveMaxLength(128);
        builder.Properties<ResearchIterationState>().HaveConversion<string>().HaveMaxLength(16);
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

        modelBuilder.Entity<ResearchIteration>(entity =>
        {
            entity.HasOne(r => r.Experiment)
                .WithMany(p => p.ResearchIterations)
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
            entity.HasOne(r => r.ResearchIteration)
                .WithMany()
                .HasForeignKey(r => r.ResearchIterationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.ResearchIterationId).IsUnique();
        });

        modelBuilder.Entity<Hypothesis>(entity =>
        {
            entity.Property(h => h.Id).ValueGeneratedOnAdd();

            entity.HasOne(h => h.ResearchIteration)
                .WithMany(r => r.Hypotheses)
                .HasForeignKey(h => h.ResearchIterationId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction: avoids SQL Server cycle with CASCADE from ResearchIteration on the same table.
            entity.HasOne(h => h.BuilOptimizationHypothesis)
                .WithMany()
                .HasForeignKey(h => h.BuildsOnHypothesisId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction: SQL Server rejects multiple cascade/set-null paths when combined with CASCADE from ResearchIteration.
            entity.HasOne(h => h.BenchmarkRunBefore)
                .WithMany()
                .HasForeignKey(h => h.BenchmarkRunIdBefore)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(h => h.BenchmarkRunAfter)
                .WithMany()
                .HasForeignKey(h => h.BenchmarkRunIdAfter)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<HypothesisLog>(entity =>
        {
            entity.Property(l => l.Id).ValueGeneratedOnAdd();

            entity.Property(l => l.Message).HasColumnType("nvarchar(max)");

            entity.HasOne(l => l.Hypothesis)
                .WithMany(h => h.Logs)
                .HasForeignKey(l => l.HypothesisId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
