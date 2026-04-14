using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeEngineAdded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaselineBenchmarkRunId",
                table: "ResearchIterations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredBaseTables",
                table: "ResearchIterations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaDiscoveryMarkdown",
                table: "ResearchIterations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaDiscoveryResultJson",
                table: "ResearchIterations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Hypotheses",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<int>(
                name: "OptimizeRetryCount",
                table: "Hypotheses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OptimizeSql",
                table: "Hypotheses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevertRetryCount",
                table: "Hypotheses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RevertSql",
                table: "Hypotheses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_BaselineBenchmarkRunId",
                table: "ResearchIterations",
                column: "BaselineBenchmarkRunId");

            migrationBuilder.AddForeignKey(
                name: "FK_ResearchIterations_BenchmarkRuns_BaselineBenchmarkRunId",
                table: "ResearchIterations",
                column: "BaselineBenchmarkRunId",
                principalTable: "BenchmarkRuns",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResearchIterations_BenchmarkRuns_BaselineBenchmarkRunId",
                table: "ResearchIterations");

            migrationBuilder.DropIndex(
                name: "IX_ResearchIterations_BaselineBenchmarkRunId",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "BaselineBenchmarkRunId",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "RegisteredBaseTables",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "SchemaDiscoveryMarkdown",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "SchemaDiscoveryResultJson",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "OptimizeRetryCount",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "OptimizeSql",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "RevertRetryCount",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "RevertSql",
                table: "Hypotheses");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Hypotheses",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);
        }
    }
}
