using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <summary>
    /// Repairs identity seeds on tables rebuilt by <c>HypothesisIdIdentity</c> and
    /// <c>EntityPrimaryKeyIdentity</c>. Those migrations reseeded with
    /// <c>DBCC CHECKIDENT(..., RESEED, MAX(Id))</c>; on a freshly rebuilt table SQL
    /// Server treats the reseed as "no rows inserted yet", so the NEXT generated
    /// identity equals the reseed value itself rather than reseed+1. Tables whose
    /// legacy rows had MAX(Id)=0 (rows created before identity, when EF sent
    /// explicit 0) therefore generate 0 on the next insert and hit a duplicate
    /// primary key violation.
    ///
    /// Reseeding to MAX(Id)+1 sidesteps the ambiguity in every table state: the
    /// next generated value is MAX+1 (virgin) or MAX+2 (a harmless gap), never a
    /// collision. Existing rows keep their ids, including legacy Id=0 rows.
    /// </summary>
    public partial class FixIdentityReseedAfterRebuild : Migration
    {
        private static readonly string[] RebuiltTables =
        [
            "DatabaseConnections",
            "AIConnections",
            "BenchmarkRuns",
            "Experiments",
            "ResearchIterations",
            "RunQueue",
            "Hypotheses",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in RebuiltTables)
            {
                // DBCC CHECKIDENT only accepts a constant or plain variable as the
                // reseed value, so the +1 must be computed into the variable first.
                // Per-table variable names keep the statements valid even when all
                // operations are emitted into a single script batch.
                migrationBuilder.Sql($"""
                    DECLARE @max_{table} int = (SELECT ISNULL(MAX([Id]), 0) FROM [{table}]);
                    SET @max_{table} = @max_{table} + 1;
                    DBCC CHECKIDENT ('[{table}]', RESEED, @max_{table});
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reseeding is not meaningfully reversible; identity gaps are harmless.
        }
    }
}
