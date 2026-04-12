using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations;

/// <summary>
/// Renames hypothesis batch storage to research iterations (table + FK columns + indexes).
/// </summary>
public partial class RenameHypothesisBatchToResearchIteration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(
            name: "HypothesisBatches",
            newName: "ResearchIterations");

        migrationBuilder.RenameColumn(
            name: "HypothesisBatchId",
            table: "Hypotheses",
            newName: "ResearchIterationId");

        migrationBuilder.RenameColumn(
            name: "HypothesisBatchId",
            table: "RunQueue",
            newName: "ResearchIterationId");

        migrationBuilder.RenameIndex(
            name: "IX_Hypotheses_HypothesisBatchId",
            table: "Hypotheses",
            newName: "IX_Hypotheses_ResearchIterationId");

        migrationBuilder.RenameIndex(
            name: "IX_RunQueue_HypothesisBatchId",
            table: "RunQueue",
            newName: "IX_RunQueue_ResearchIterationId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameIndex(
            name: "IX_Hypotheses_ResearchIterationId",
            table: "Hypotheses",
            newName: "IX_Hypotheses_HypothesisBatchId");

        migrationBuilder.RenameIndex(
            name: "IX_RunQueue_ResearchIterationId",
            table: "RunQueue",
            newName: "IX_RunQueue_HypothesisBatchId");

        migrationBuilder.RenameColumn(
            name: "ResearchIterationId",
            table: "Hypotheses",
            newName: "HypothesisBatchId");

        migrationBuilder.RenameColumn(
            name: "ResearchIterationId",
            table: "RunQueue",
            newName: "HypothesisBatchId");

        migrationBuilder.RenameTable(
            name: "ResearchIterations",
            newName: "HypothesisBatches");
    }
}
