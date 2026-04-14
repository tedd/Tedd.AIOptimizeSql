using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddModifiedAtWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "BenchmarkRuns",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "Hypotheses",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "HypothesisLogs",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "ResearchIterations",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "RunQueue",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "RunQueue");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "HypothesisLogs");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "BenchmarkRuns");
        }
    }
}
