using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnalyzeOnly",
                table: "DatabaseConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DatabaseAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    DatabaseConnectionId = table.Column<int>(type: "int", nullable: true),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EnableWebSearch = table.Column<bool>(type: "bit", nullable: false),
                    IncludeStoredProceduresAndViews = table.Column<bool>(type: "bit", nullable: false),
                    MaxAiFindings = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricsSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricsSummaryMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiSummaryMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                name: "AnalysisFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseAnalysisId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Recommendation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecommendationSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ObjectSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ObjectName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ImpactScore = table.Column<double>(type: "float", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProposedExperimentId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DatabaseAnalysisLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseAnalysisId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisFindings");

            migrationBuilder.DropTable(
                name: "DatabaseAnalysisLogs");

            migrationBuilder.DropTable(
                name: "DatabaseAnalyses");

            migrationBuilder.DropColumn(
                name: "AnalyzeOnly",
                table: "DatabaseConnections");
        }
    }
}
