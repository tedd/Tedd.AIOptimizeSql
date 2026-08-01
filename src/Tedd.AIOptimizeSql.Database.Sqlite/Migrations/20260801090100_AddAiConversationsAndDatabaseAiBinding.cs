using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConversationsAndDatabaseAiBinding : Migration
    {
        // DatabaseConnections.AIConnectionId is added as a plain indexed column, without the
        // REFERENCES clause the model declares. SQLite cannot attach a foreign key to an
        // existing table; doing it properly means dropping and recreating DatabaseConnections
        // on the user's live standalone database file, and the constraint buys nothing here:
        // "an AI still bound to a database cannot be deleted" is enforced in the application
        // (AIConnectionList offers to unbind first), which is where the user-facing behaviour
        // lives either way.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AIConnectionId",
                table: "DatabaseConnections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiConversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DatabaseConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    AIConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RequestCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RelatedDatabaseAnalysisId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedExperimentId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedResearchIterationId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedHypothesisId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

            // Existing databases predate the binding. Adopt the AI an experiment (or, failing
            // that, an analysis) on that database already used, so upgrading does not leave
            // every database unbound.
            migrationBuilder.Sql(@"
UPDATE DatabaseConnections
SET    AIConnectionId = COALESCE(
           (SELECT e.AIConnectionId FROM Experiments e
            WHERE e.DatabaseConnectionId = DatabaseConnections.Id AND e.AIConnectionId IS NOT NULL
            ORDER BY e.ModifiedAt DESC LIMIT 1),
           (SELECT a.AIConnectionId FROM DatabaseAnalyses a
            WHERE a.DatabaseConnectionId = DatabaseConnections.Id AND a.AIConnectionId IS NOT NULL
            ORDER BY a.ModifiedAt DESC LIMIT 1))
WHERE  AIConnectionId IS NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
