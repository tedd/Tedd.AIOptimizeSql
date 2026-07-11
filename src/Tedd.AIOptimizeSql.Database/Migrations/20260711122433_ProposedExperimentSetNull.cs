using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class ProposedExperimentSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisFindings_Experiments_ProposedExperimentId",
                table: "AnalysisFindings");

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisFindings_Experiments_ProposedExperimentId",
                table: "AnalysisFindings",
                column: "ProposedExperimentId",
                principalTable: "Experiments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisFindings_Experiments_ProposedExperimentId",
                table: "AnalysisFindings");

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisFindings_Experiments_ProposedExperimentId",
                table: "AnalysisFindings",
                column: "ProposedExperimentId",
                principalTable: "Experiments",
                principalColumn: "Id");
        }
    }
}
