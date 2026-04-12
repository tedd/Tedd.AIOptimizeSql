using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddHypothesisStatusAndErrorMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "Hypotheses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Hypotheses",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Pending");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "Hypotheses");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Hypotheses");
        }
    }
}
