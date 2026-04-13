using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    public partial class HypothesisIdIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot add IDENTITY via ALTER COLUMN; rebuild the table and preserve rows + FK names.
            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdAfter",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdBefore",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                table: "Hypotheses");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Hypotheses",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BenchmarkRunIdAfter",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BenchmarkRunIdBefore",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_ResearchIterationId",
                table: "Hypotheses");

            migrationBuilder.RenameTable(
                name: "Hypotheses",
                newName: "Hypotheses_old");

            migrationBuilder.CreateTable(
                name: "Hypotheses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ResearchIterationId = table.Column<int>(type: "int", nullable: false),
                    BenchmarkRunIdBefore = table.Column<int>(type: "int", nullable: true),
                    BenchmarkRunIdAfter = table.Column<int>(type: "int", nullable: true),
                    ImpovementPercentage = table.Column<float>(type: "real", nullable: false),
                    BuildsOnHypothesisId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TimeUsedMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
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
                        name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                        column: x => x.ResearchIterationId,
                        principalTable: "ResearchIterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_Hypotheses_ResearchIterationId",
                table: "Hypotheses",
                column: "ResearchIterationId");

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [Hypotheses] ON;
                INSERT INTO [Hypotheses] ([Id], [ResearchIterationId], [BenchmarkRunIdBefore], [BenchmarkRunIdAfter], [ImpovementPercentage], [BuildsOnHypothesisId], [Description], [TimeUsedMs], [CreatedAt], [ErrorMessage], [Status])
                SELECT [Id], [ResearchIterationId], [BenchmarkRunIdBefore], [BenchmarkRunIdAfter], [ImpovementPercentage], [BuildsOnHypothesisId], [Description], [TimeUsedMs], [CreatedAt], [ErrorMessage], [Status]
                FROM [Hypotheses_old];
                SET IDENTITY_INSERT [Hypotheses] OFF;
                IF EXISTS (SELECT 1 FROM [Hypotheses])
                BEGIN
                    DECLARE @m int = (SELECT MAX([Id]) FROM [Hypotheses]);
                    DBCC CHECKIDENT ('[Hypotheses]', RESEED, @m);
                END
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Hypotheses_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses",
                column: "BuildsOnHypothesisId",
                principalTable: "Hypotheses",
                principalColumn: "Id");

            migrationBuilder.DropTable(
                name: "Hypotheses_old");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdAfter",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdBefore",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                table: "Hypotheses");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Hypotheses",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BenchmarkRunIdAfter",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BenchmarkRunIdBefore",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses");

            migrationBuilder.DropIndex(
                name: "IX_Hypotheses_ResearchIterationId",
                table: "Hypotheses");

            migrationBuilder.RenameTable(
                name: "Hypotheses",
                newName: "Hypotheses_old");

            migrationBuilder.CreateTable(
                name: "Hypotheses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ResearchIterationId = table.Column<int>(type: "int", nullable: false),
                    BenchmarkRunIdBefore = table.Column<int>(type: "int", nullable: true),
                    BenchmarkRunIdAfter = table.Column<int>(type: "int", nullable: true),
                    ImpovementPercentage = table.Column<float>(type: "real", nullable: false),
                    BuildsOnHypothesisId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TimeUsedMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
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
                        name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                        column: x => x.ResearchIterationId,
                        principalTable: "ResearchIterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_Hypotheses_ResearchIterationId",
                table: "Hypotheses",
                column: "ResearchIterationId");

            migrationBuilder.Sql(
                """
                INSERT INTO [Hypotheses] ([Id], [ResearchIterationId], [BenchmarkRunIdBefore], [BenchmarkRunIdAfter], [ImpovementPercentage], [BuildsOnHypothesisId], [Description], [TimeUsedMs], [CreatedAt], [ErrorMessage], [Status])
                SELECT [Id], [ResearchIterationId], [BenchmarkRunIdBefore], [BenchmarkRunIdAfter], [ImpovementPercentage], [BuildsOnHypothesisId], [Description], [TimeUsedMs], [CreatedAt], [ErrorMessage], [Status]
                FROM [Hypotheses_old];
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Hypotheses_Hypotheses_BuildsOnHypothesisId",
                table: "Hypotheses",
                column: "BuildsOnHypothesisId",
                principalTable: "Hypotheses",
                principalColumn: "Id");

            migrationBuilder.DropTable(
                name: "Hypotheses_old");
        }
    }
}
