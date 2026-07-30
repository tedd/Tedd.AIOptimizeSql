using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlBrowserAndExperimentIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaselineOutputHash",
                table: "ResearchIterations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SandboxProvisioned",
                table: "ResearchIterations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OutputHash",
                table: "Hypotheses",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OutputMatchesBaseline",
                table: "Hypotheses",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsolationMode",
                table: "Experiments",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                // "" would not round-trip through the enum converter; existing experiments
                // keep their current behaviour, which is the un-isolated one.
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "OutputVerificationMode",
                table: "Experiments",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "OutputVerificationSql",
                table: "Experiments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxDatabaseName",
                table: "Experiments",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxSchemaName",
                table: "Experiments",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxSetupSql",
                table: "Experiments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxTeardownSql",
                table: "Experiments",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineOutputHash",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "SandboxProvisioned",
                table: "ResearchIterations");

            migrationBuilder.DropColumn(
                name: "OutputHash",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "OutputMatchesBaseline",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "IsolationMode",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "OutputVerificationMode",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "OutputVerificationSql",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "SandboxDatabaseName",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "SandboxSchemaName",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "SandboxSetupSql",
                table: "Experiments");

            migrationBuilder.DropColumn(
                name: "SandboxTeardownSql",
                table: "Experiments");
        }
    }
}
