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
    public DbSet<ResearchIterationLog> ResearchIterationLogs => Set<ResearchIterationLog>();
    public DbSet<BenchmarkRun> BenchmarkRuns => Set<BenchmarkRun>();
    public DbSet<RunQueue> RunQueue => Set<RunQueue>();
    public DbSet<DatabaseAnalysis> DatabaseAnalyses => Set<DatabaseAnalysis>();
    public DbSet<AnalysisFinding> AnalysisFindings => Set<AnalysisFinding>();
    public DbSet<DatabaseAnalysisLog> DatabaseAnalysisLogs => Set<DatabaseAnalysisLog>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DatabaseConnectionId>().HaveConversion<int>();
        builder.Properties<AIConnectionId>().HaveConversion<int>();
        builder.Properties<ExperimentId>().HaveConversion<int>();
        builder.Properties<ResearchIterationId>().HaveConversion<int>();
        builder.Properties<HypothesisId>().HaveConversion<int>();
        builder.Properties<HypothesisLogId>().HaveConversion<int>();
        builder.Properties<ResearchIterationLogId>().HaveConversion<int>();
        builder.Properties<BenchmarkRunId>().HaveConversion<int>();
        builder.Properties<RunQueueId>().HaveConversion<int>();
        builder.Properties<DatabaseAnalysisId>().HaveConversion<int>();
        builder.Properties<AnalysisFindingId>().HaveConversion<int>();
        builder.Properties<DatabaseAnalysisLogId>().HaveConversion<int>();
        builder.Properties<AgentTaskId>().HaveConversion<int>();
        builder.Properties<AiConversationId>().HaveConversion<int>();

        // AiProvider enum stored as string in DB
        builder.Properties<AiProvider>().HaveConversion<string>().HaveMaxLength(128);
        builder.Properties<ResearchIterationState>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<HypothesisState>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<DatabaseAnalysisState>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<FindingSeverity>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<FindingCategory>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AgentTaskStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<ExperimentIsolationMode>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<OutputVerificationMode>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AiConversationKind>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AiConversationState>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // UseIdentityColumn is SQL Server-only; under SQLite the leftover annotation
        // suppresses autoincrement inference and causes phantom pending-model changes.
        var isSqlServer = Database.IsSqlServer();

        modelBuilder.Entity<DatabaseConnection>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            // Restrict, not SetNull: an AI connection a database still uses must not be
            // deletable behind the user's back. The UI offers to unbind first.
            entity.HasOne(e => e.AIConnection)
                .WithMany()
                .HasForeignKey(e => e.AIConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AIConnection>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<Experiment>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            if (isSqlServer)
            {
                entity.Property(e => e.SandboxSetupSql).HasColumnType("nvarchar(max)");
                entity.Property(e => e.SandboxTeardownSql).HasColumnType("nvarchar(max)");
                entity.Property(e => e.OutputVerificationSql).HasColumnType("nvarchar(max)");
            }

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
            // Enum PK: CLR default is 0, which EF can treat as an explicit key — INSERT then sends Id=0 and breaks IDENTITY / duplicates.
            var researchIterationId = entity.Property(r => r.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(ResearchIterationId.Transient);
            if (isSqlServer)
                researchIterationId.UseIdentityColumn();

            entity.HasOne(r => r.Experiment)
                .WithMany(p => p.ResearchIterations)
                .HasForeignKey(r => r.ExperimentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.AIConnection)
                .WithMany()
                .HasForeignKey(r => r.AIConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.BaselineBenchmarkRun)
                .WithMany()
                .HasForeignKey(r => r.BaselineBenchmarkRunId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<BenchmarkRun>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            var actualPlanXml = entity.Property(e => e.ActualPlanXml)
                .HasConversion(
                    v => JsonSerializer.Serialize(v),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>());
            if (isSqlServer)
                actualPlanXml.HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<RunQueue>(entity =>
        {
            entity.Property(r => r.Id).ValueGeneratedOnAdd();

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

            if (isSqlServer)
                entity.Property(l => l.Message).HasColumnType("nvarchar(max)");

            entity.HasOne(l => l.Hypothesis)
                .WithMany(h => h.Logs)
                .HasForeignKey(l => l.HypothesisId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResearchIterationLog>(entity =>
        {
            var researchIterationLogId = entity.Property(l => l.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(ResearchIterationLogId.Transient);
            if (isSqlServer)
                researchIterationLogId.UseIdentityColumn();

            if (isSqlServer)
                entity.Property(l => l.Message).HasColumnType("nvarchar(max)");

            entity.HasOne(l => l.ResearchIteration)
                .WithMany(r => r.Logs)
                .HasForeignKey(l => l.ResearchIterationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DatabaseAnalysis>(entity =>
        {
            // Enum PK: CLR default is 0, which EF can treat as an explicit key — INSERT then sends Id=0 and breaks IDENTITY / duplicates.
            var databaseAnalysisId = entity.Property(a => a.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(DatabaseAnalysisId.Transient);
            if (isSqlServer)
                databaseAnalysisId.UseIdentityColumn();

            entity.HasOne(a => a.DatabaseConnection)
                .WithMany()
                .HasForeignKey(a => a.DatabaseConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(a => a.AIConnection)
                .WithMany()
                .HasForeignKey(a => a.AIConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AnalysisFinding>(entity =>
        {
            var analysisFindingId = entity.Property(f => f.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(AnalysisFindingId.Transient);
            if (isSqlServer)
                analysisFindingId.UseIdentityColumn();

            entity.HasOne(f => f.DatabaseAnalysis)
                .WithMany(a => a.Findings)
                .HasForeignKey(f => f.DatabaseAnalysisId)
                .OnDelete(DeleteBehavior.Cascade);

            // SetNull: deleting a proposed experiment keeps the finding and just clears the link.
            entity.HasOne(f => f.ProposedExperiment)
                .WithMany()
                .HasForeignKey(f => f.ProposedExperimentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DatabaseAnalysisLog>(entity =>
        {
            var databaseAnalysisLogId = entity.Property(l => l.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(DatabaseAnalysisLogId.Transient);
            if (isSqlServer)
                databaseAnalysisLogId.UseIdentityColumn();

            if (isSqlServer)
                entity.Property(l => l.Message).HasColumnType("nvarchar(max)");

            entity.HasOne(l => l.DatabaseAnalysis)
                .WithMany(a => a.Logs)
                .HasForeignKey(l => l.DatabaseAnalysisId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentTask>(entity =>
        {
            var agentTaskId = entity.Property(t => t.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(AgentTaskId.Transient);
            if (isSqlServer)
                agentTaskId.UseIdentityColumn();

            entity.HasOne(t => t.DatabaseAnalysis)
                .WithMany()
                .HasForeignKey(t => t.DatabaseAnalysisId)
                .OnDelete(DeleteBehavior.Cascade);

            // Cascade is safe here: the cascade paths into AgentTasks (via
            // DatabaseAnalyses and via Experiments→ResearchIterations→Hypotheses)
            // have no shared ancestor, so SQL Server accepts both.
            entity.HasOne(t => t.Hypothesis)
                .WithMany()
                .HasForeignKey(t => t.HypothesisId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => t.DatabaseAnalysisId);
            entity.HasIndex(t => t.HypothesisId);
        });

        modelBuilder.Entity<AiConversation>(entity =>
        {
            var aiConversationId = entity.Property(c => c.Id)
                .ValueGeneratedOnAdd()
                .HasSentinel(AiConversationId.Transient);
            if (isSqlServer)
                aiConversationId.UseIdentityColumn();

            if (isSqlServer)
                entity.Property(c => c.LastMessage).HasColumnType("nvarchar(max)");

            // SetNull on both: the usage ledger outlives the connections it records, so a
            // deleted connection leaves the historic spend readable through the snapshotted
            // provider/model instead of taking the row with it.
            entity.HasOne(c => c.DatabaseConnection)
                .WithMany()
                .HasForeignKey(c => c.DatabaseConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.AIConnection)
                .WithMany()
                .HasForeignKey(c => c.AIConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(c => c.DatabaseConnectionId);
            entity.HasIndex(c => c.StartedAt);
        });
    }
}
