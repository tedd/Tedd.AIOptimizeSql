using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AIConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalServerCpuTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalServerElapsedTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalScanCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLogicalReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPhysicalReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPageServerReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalReadAheadReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPageServerReadAheadReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLobLogicalReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLobPhysicalReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLobPageServerReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLobReadAheadReads = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLobPageServerReadAheadReads = table.Column<int>(type: "INTEGER", nullable: false),
                    ActualPlanXml = table.Column<string>(type: "TEXT", nullable: false),
                    Messages = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ConnectionString = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    AnalyzeOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DatabaseConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    AIConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    EnableWebSearch = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeStoredProceduresAndViews = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxAiFindings = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    MetricsSnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    MetricsSummaryMarkdown = table.Column<string>(type: "TEXT", nullable: true),
                    AiSummaryMarkdown = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseAnalyses_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DatabaseAnalyses_DatabaseConnections_DatabaseConnectionId",
                        column: x => x.DatabaseConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DatabaseConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    ExperimentPreRunSql = table.Column<string>(type: "TEXT", nullable: true),
                    ExperimentPostRunSql = table.Column<string>(type: "TEXT", nullable: true),
                    HypothesisPreRunSql = table.Column<string>(type: "TEXT", nullable: true),
                    HypothesisPostRunSql = table.Column<string>(type: "TEXT", nullable: true),
                    BenchmarkSql = table.Column<string>(type: "TEXT", nullable: true),
                    AIConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Experiments_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Experiments_DatabaseConnections_DatabaseConnectionId",
                        column: x => x.DatabaseConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseAnalysisLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DatabaseAnalysisId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseAnalysisLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseAnalysisLogs_DatabaseAnalyses_DatabaseAnalysisId",
                        column: x => x.DatabaseAnalysisId,
                        principalTable: "DatabaseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DatabaseAnalysisId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", nullable: true),
                    Recommendation = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendationSql = table.Column<string>(type: "TEXT", nullable: true),
                    ObjectSchema = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ObjectName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ImpactScore = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProposedExperimentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisFindings_DatabaseAnalyses_DatabaseAnalysisId",
                        column: x => x.DatabaseAnalysisId,
                        principalTable: "DatabaseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnalysisFindings_Experiments_ProposedExperimentId",
                        column: x => x.ProposedExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ResearchIterations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ExperimentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hints = table.Column<string>(type: "TEXT", nullable: true),
                    AIConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    AiProviderUsed = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AiModelUsed = table.Column<string>(type: "TEXT", nullable: true),
                    MaxNumberOfHypotheses = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SchemaDiscoveryMarkdown = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaDiscoveryResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    RegisteredBaseTables = table.Column<string>(type: "TEXT", nullable: true),
                    BaselineBenchmarkRunId = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchIterations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchIterations_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResearchIterations_BenchmarkRuns_BaselineBenchmarkRunId",
                        column: x => x.BaselineBenchmarkRunId,
                        principalTable: "BenchmarkRuns",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResearchIterations_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Hypotheses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ResearchIterationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BenchmarkRunIdBefore = table.Column<int>(type: "INTEGER", nullable: true),
                    BenchmarkRunIdAfter = table.Column<int>(type: "INTEGER", nullable: true),
                    ImpovementPercentage = table.Column<float>(type: "REAL", nullable: false),
                    BuildsOnHypothesisId = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    OptimizeSql = table.Column<string>(type: "TEXT", nullable: true),
                    RevertSql = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    OptimizeRetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RevertRetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeUsedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hypotheses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdAfter",
                        column: x => x.BenchmarkRunIdAfter,
                        principalTable: "BenchmarkRuns",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdBefore",
                        column: x => x.BenchmarkRunIdBefore,
                        principalTable: "BenchmarkRuns",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Hypotheses_Hypotheses_BuildsOnHypothesisId",
                        column: x => x.BuildsOnHypothesisId,
                        principalTable: "Hypotheses",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Hypotheses_ResearchIterations_ResearchIterationId",
                        column: x => x.ResearchIterationId,
                        principalTable: "ResearchIterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunQueue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ResearchIterationId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunQueue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunQueue_ResearchIterations_ResearchIterationId",
                        column: x => x.ResearchIterationId,
                        principalTable: "ResearchIterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DatabaseAnalysisId = table.Column<int>(type: "INTEGER", nullable: true),
                    HypothesisId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTasks_DatabaseAnalyses_DatabaseAnalysisId",
                        column: x => x.DatabaseAnalysisId,
                        principalTable: "DatabaseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentTasks_Hypotheses_HypothesisId",
                        column: x => x.HypothesisId,
                        principalTable: "Hypotheses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HypothesisLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    HypothesisId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HypothesisLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HypothesisLogs_Hypotheses_HypothesisId",
                        column: x => x.HypothesisId,
                        principalTable: "Hypotheses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_DatabaseAnalysisId",
                table: "AgentTasks",
                column: "DatabaseAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_HypothesisId",
                table: "AgentTasks",
                column: "HypothesisId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisFindings_DatabaseAnalysisId",
                table: "AnalysisFindings",
                column: "DatabaseAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisFindings_ProposedExperimentId",
                table: "AnalysisFindings",
                column: "ProposedExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAnalyses_AIConnectionId",
                table: "DatabaseAnalyses",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAnalyses_DatabaseConnectionId",
                table: "DatabaseAnalyses",
                column: "DatabaseConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAnalysisLogs_DatabaseAnalysisId",
                table: "DatabaseAnalysisLogs",
                column: "DatabaseAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_AIConnectionId",
                table: "Experiments",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_DatabaseConnectionId",
                table: "Experiments",
                column: "DatabaseConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Hypotheses_BenchmarkRunIdAfter",
                table: "Hypotheses",
                column: "BenchmarkRunIdAfter");

            migrationBuilder.CreateIndex(
                name: "IX_Hypotheses_BenchmarkRunIdBefore",
                table: "Hypotheses",
                column: "BenchmarkRunIdBefore");

            migrationBuilder.CreateIndex(
                name: "IX_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses",
                column: "BuildsOnHypothesisId");

            migrationBuilder.CreateIndex(
                name: "IX_Hypotheses_ResearchIterationId",
                table: "Hypotheses",
                column: "ResearchIterationId");

            migrationBuilder.CreateIndex(
                name: "IX_HypothesisLogs_HypothesisId",
                table: "HypothesisLogs",
                column: "HypothesisId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_AIConnectionId",
                table: "ResearchIterations",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_BaselineBenchmarkRunId",
                table: "ResearchIterations",
                column: "BaselineBenchmarkRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_ExperimentId",
                table: "ResearchIterations",
                column: "ExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_RunQueue_ResearchIterationId",
                table: "RunQueue",
                column: "ResearchIterationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropTable(
                name: "AnalysisFindings");

            migrationBuilder.DropTable(
                name: "DatabaseAnalysisLogs");

            migrationBuilder.DropTable(
                name: "HypothesisLogs");

            migrationBuilder.DropTable(
                name: "RunQueue");

            migrationBuilder.DropTable(
                name: "DatabaseAnalyses");

            migrationBuilder.DropTable(
                name: "Hypotheses");

            migrationBuilder.DropTable(
                name: "ResearchIterations");

            migrationBuilder.DropTable(
                name: "BenchmarkRuns");

            migrationBuilder.DropTable(
                name: "Experiments");

            migrationBuilder.DropTable(
                name: "AIConnections");

            migrationBuilder.DropTable(
                name: "DatabaseConnections");
        }
    }
}
