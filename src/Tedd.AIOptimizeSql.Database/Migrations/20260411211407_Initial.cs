using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
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
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TotalTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalServerCpuTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalServerElapsedTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalScanCount = table.Column<int>(type: "int", nullable: false),
                    TotalLogicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalPhysicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalPageServerReads = table.Column<int>(type: "int", nullable: false),
                    TotalReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalPageServerReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobLogicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPhysicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPageServerReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPageServerReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    ActualPlanXml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Messages = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    DatabaseConnectionId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExperimentPreRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExperimentPostRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HypothesisPreRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HypothesisPostRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BenchmarkSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                name: "HypothesisBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ExperimentId = table.Column<int>(type: "int", nullable: false),
                    Hints = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    AiProviderUsed = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AiModelUsed = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaxNumberOfHypotheses = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HypothesisBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HypothesisBatches_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HypothesisBatches_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Hypotheses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    HypothesisBatchId = table.Column<int>(type: "int", nullable: false),
                    BenchmarkRunIdBefore = table.Column<int>(type: "int", nullable: true),
                    BenchmarkRunIdAfter = table.Column<int>(type: "int", nullable: true),
                    ImpovementPercentage = table.Column<float>(type: "real", nullable: false),
                    BuildsOnHypothesisId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TimeUsedMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                        name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                        column: x => x.HypothesisBatchId,
                        principalTable: "HypothesisBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunQueue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    HypothesisBatchId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunQueue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunQueue_HypothesisBatches_HypothesisBatchId",
                        column: x => x.HypothesisBatchId,
                        principalTable: "HypothesisBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_Hypotheses_HypothesisBatchId",
                table: "Hypotheses",
                column: "HypothesisBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_HypothesisBatches_AIConnectionId",
                table: "HypothesisBatches",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HypothesisBatches_ExperimentId",
                table: "HypothesisBatches",
                column: "ExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_RunQueue_HypothesisBatchId",
                table: "RunQueue",
                column: "HypothesisBatchId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Hypotheses");

            migrationBuilder.DropTable(
                name: "RunQueue");

            migrationBuilder.DropTable(
                name: "BenchmarkRuns");

            migrationBuilder.DropTable(
                name: "HypothesisBatches");

            migrationBuilder.DropTable(
                name: "Experiments");

            migrationBuilder.DropTable(
                name: "AIConnections");

            migrationBuilder.DropTable(
                name: "DatabaseConnections");
        }
    }
}
