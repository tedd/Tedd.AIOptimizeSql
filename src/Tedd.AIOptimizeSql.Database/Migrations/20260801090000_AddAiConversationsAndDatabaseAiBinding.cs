using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConversationsAndDatabaseAiBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AIConnectionId",
                table: "DatabaseConnections",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiConversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseConnectionId = table.Column<int>(type: "int", nullable: true),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: false),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: false),
                    RelatedDatabaseAnalysisId = table.Column<int>(type: "int", nullable: true),
                    RelatedExperimentId = table.Column<int>(type: "int", nullable: true),
                    RelatedResearchIterationId = table.Column<int>(type: "int", nullable: true),
                    RelatedHypothesisId = table.Column<int>(type: "int", nullable: true),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiConversations_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AiConversations_DatabaseConnections_DatabaseConnectionId",
                        column: x => x.DatabaseConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseConnections_AIConnectionId",
                table: "DatabaseConnections",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_AIConnectionId",
                table: "AiConversations",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_DatabaseConnectionId",
                table: "AiConversations",
                column: "DatabaseConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_StartedAt",
                table: "AiConversations",
                column: "StartedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_DatabaseConnections_AIConnections_AIConnectionId",
                table: "DatabaseConnections",
                column: "AIConnectionId",
                principalTable: "AIConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Existing databases predate the binding. Adopt the AI an experiment on that
            // database already used, so upgrading does not leave every database unbound.
            migrationBuilder.Sql(@"
UPDATE d
SET    d.AIConnectionId = x.AIConnectionId
FROM   DatabaseConnections d
CROSS APPLY (
    SELECT TOP (1) e.AIConnectionId
    FROM   Experiments e
    WHERE  e.DatabaseConnectionId = d.Id AND e.AIConnectionId IS NOT NULL
    ORDER  BY e.ModifiedAt DESC
) x
WHERE  d.AIConnectionId IS NULL;

UPDATE d
SET    d.AIConnectionId = x.AIConnectionId
FROM   DatabaseConnections d
CROSS APPLY (
    SELECT TOP (1) a.AIConnectionId
    FROM   DatabaseAnalyses a
    WHERE  a.DatabaseConnectionId = d.Id AND a.AIConnectionId IS NOT NULL
    ORDER  BY a.ModifiedAt DESC
) x
WHERE  d.AIConnectionId IS NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DatabaseConnections_AIConnections_AIConnectionId",
                table: "DatabaseConnections");

            migrationBuilder.DropTable(
                name: "AiConversations");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseConnections_AIConnectionId",
                table: "DatabaseConnections");

            migrationBuilder.DropColumn(
                name: "AIConnectionId",
                table: "DatabaseConnections");
        }
    }
}
