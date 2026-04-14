using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tedd.AIOptimizeSql.Database.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Rebuilds tables whose <c>Id</c> was created without SQL Server IDENTITY (original <c>Initial</c> migration).
    /// Strongly typed enum PKs skip EF int identity conventions, so inserts used <c>Id = 0</c> or, after
    /// <see cref="Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder.ValueGeneratedOnAdd"/>, omitted <c>Id</c> and failed.
    /// </summary>
    public partial class EntityPrimaryKeyIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdAfter",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdBefore",
                table: "Hypotheses");

            migrationBuilder.DropForeignKey(
                name: "FK_RunQueue_HypothesisBatches_HypothesisBatchId",
                table: "RunQueue");

            migrationBuilder.DropForeignKey(
                name: "FK_ResearchIterations_BenchmarkRuns_BaselineBenchmarkRunId",
                table: "ResearchIterations");

            migrationBuilder.DropForeignKey(
                name: "FK_HypothesisBatches_AIConnections_AIConnectionId",
                table: "ResearchIterations");

            migrationBuilder.DropForeignKey(
                name: "FK_HypothesisBatches_Experiments_ExperimentId",
                table: "ResearchIterations");

            migrationBuilder.DropForeignKey(
                name: "FK_Experiments_AIConnections_AIConnectionId",
                table: "Experiments");

            migrationBuilder.DropForeignKey(
                name: "FK_Experiments_DatabaseConnections_DatabaseConnectionId",
                table: "Experiments");

            // --- DatabaseConnections ---
            migrationBuilder.RenameTable(
                name: "DatabaseConnections",
                newName: "DatabaseConnections_old");

            migrationBuilder.CreateTable(
                name: "DatabaseConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseConnections", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [DatabaseConnections] ON;
                INSERT INTO [DatabaseConnections] ([Id], [Name], [ConnectionString], [CreatedAt], [ModifiedAt])
                SELECT [Id], [Name], [ConnectionString], [CreatedAt], [ModifiedAt] FROM [DatabaseConnections_old];
                SET IDENTITY_INSERT [DatabaseConnections] OFF;
                IF EXISTS (SELECT 1 FROM [DatabaseConnections])
                BEGIN
                    DECLARE @dc int = (SELECT MAX([Id]) FROM [DatabaseConnections]);
                    DBCC CHECKIDENT ('[DatabaseConnections]', RESEED, @dc);
                END
                """);

            migrationBuilder.DropTable(name: "DatabaseConnections_old");

            // --- AIConnections ---
            migrationBuilder.RenameTable(
                name: "AIConnections",
                newName: "AIConnections_old");

            migrationBuilder.CreateTable(
                name: "AIConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIConnections", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [AIConnections] ON;
                INSERT INTO [AIConnections] ([Id], [Name], [Provider], [Model], [Endpoint], [ApiKey], [CreatedAt], [ModifiedAt])
                SELECT [Id], [Name], [Provider], [Model], [Endpoint], [ApiKey], [CreatedAt], [ModifiedAt] FROM [AIConnections_old];
                SET IDENTITY_INSERT [AIConnections] OFF;
                IF EXISTS (SELECT 1 FROM [AIConnections])
                BEGIN
                    DECLARE @ai int = (SELECT MAX([Id]) FROM [AIConnections]);
                    DBCC CHECKIDENT ('[AIConnections]', RESEED, @ai);
                END
                """);

            migrationBuilder.DropTable(name: "AIConnections_old");

            // --- BenchmarkRuns ---
            migrationBuilder.RenameTable(
                name: "BenchmarkRuns",
                newName: "BenchmarkRuns_old");

            migrationBuilder.CreateTable(
                name: "BenchmarkRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TotalTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalServerCpuTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalServerElapsedTimeMs = table.Column<int>(type: "int", nullable: false),
                    TotalScanCount = table.Column<int>(type: "int", nullable: false),
                    TotalLogicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalPhysicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalPageServerReads = table.Column<int>(type: "int", nullable: false),
                    TotalReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalPageServerReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobLogicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPhysicalReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPageServerReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    TotalLobPageServerReadAheadReads = table.Column<int>(type: "int", nullable: false),
                    ActualPlanXml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Messages = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkRuns", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [BenchmarkRuns] ON;
                INSERT INTO [BenchmarkRuns] ([Id], [TotalTimeMs], [TotalServerCpuTimeMs], [TotalServerElapsedTimeMs], [TotalScanCount], [TotalLogicalReads], [TotalPhysicalReads], [TotalPageServerReads], [TotalReadAheadReads], [TotalPageServerReadAheadReads], [TotalLobLogicalReads], [TotalLobPhysicalReads], [TotalLobPageServerReads], [TotalLobReadAheadReads], [TotalLobPageServerReadAheadReads], [ActualPlanXml], [Messages], [CreatedAt], [ModifiedAt])
                SELECT [Id], [TotalTimeMs], [TotalServerCpuTimeMs], [TotalServerElapsedTimeMs], [TotalScanCount], [TotalLogicalReads], [TotalPhysicalReads], [TotalPageServerReads], [TotalReadAheadReads], [TotalPageServerReadAheadReads], [TotalLobLogicalReads], [TotalLobPhysicalReads], [TotalLobPageServerReads], [TotalLobReadAheadReads], [TotalLobPageServerReadAheadReads], [ActualPlanXml], [Messages], [CreatedAt], [ModifiedAt]
                FROM [BenchmarkRuns_old];
                SET IDENTITY_INSERT [BenchmarkRuns] OFF;
                IF EXISTS (SELECT 1 FROM [BenchmarkRuns])
                BEGIN
                    DECLARE @br int = (SELECT MAX([Id]) FROM [BenchmarkRuns]);
                    DBCC CHECKIDENT ('[BenchmarkRuns]', RESEED, @br);
                END
                """);

            migrationBuilder.DropTable(name: "BenchmarkRuns_old");

            // --- Experiments ---
            migrationBuilder.RenameTable(
                name: "Experiments",
                newName: "Experiments_old");

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseConnectionId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExperimentPreRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExperimentPostRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HypothesisPreRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HypothesisPostRunSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BenchmarkSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Experiments_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Experiments_DatabaseConnections_DatabaseConnectionId",
                        column: x => x.DatabaseConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_AIConnectionId",
                table: "Experiments",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_DatabaseConnectionId",
                table: "Experiments",
                column: "DatabaseConnectionId");

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [Experiments] ON;
                INSERT INTO [Experiments] ([Id], [DatabaseConnectionId], [Name], [Description], [Instructions], [ExperimentPreRunSql], [ExperimentPostRunSql], [HypothesisPreRunSql], [HypothesisPostRunSql], [BenchmarkSql], [AIConnectionId], [CreatedAt], [ModifiedAt])
                SELECT [Id], [DatabaseConnectionId], [Name], [Description], [Instructions], [ExperimentPreRunSql], [ExperimentPostRunSql], [HypothesisPreRunSql], [HypothesisPostRunSql], [BenchmarkSql], [AIConnectionId], [CreatedAt], [ModifiedAt]
                FROM [Experiments_old];
                SET IDENTITY_INSERT [Experiments] OFF;
                IF EXISTS (SELECT 1 FROM [Experiments])
                BEGIN
                    DECLARE @ex int = (SELECT MAX([Id]) FROM [Experiments]);
                    DBCC CHECKIDENT ('[Experiments]', RESEED, @ex);
                END
                """);

            migrationBuilder.DropTable(name: "Experiments_old");

            // --- ResearchIterations + RunQueue ---
            migrationBuilder.RenameTable(
                name: "ResearchIterations",
                newName: "ResearchIterations_old");

            migrationBuilder.CreateTable(
                name: "ResearchIterations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AIConnectionId = table.Column<int>(type: "int", nullable: true),
                    AiModelUsed = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiProviderUsed = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BaselineBenchmarkRunId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExperimentId = table.Column<int>(type: "int", nullable: false),
                    Hints = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaxNumberOfHypotheses = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RegisteredBaseTables = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaDiscoveryMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaDiscoveryResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchIterations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HypothesisBatches_AIConnections_AIConnectionId",
                        column: x => x.AIConnectionId,
                        principalTable: "AIConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HypothesisBatches_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchIterations_BenchmarkRuns_BaselineBenchmarkRunId",
                        column: x => x.BaselineBenchmarkRunId,
                        principalTable: "BenchmarkRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_AIConnectionId",
                table: "ResearchIterations",
                column: "AIConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_BaselineBenchmarkRunId",
                table: "ResearchIterations",
                column: "BaselineBenchmarkRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchIterations_ExperimentId",
                table: "ResearchIterations",
                column: "ExperimentId");

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [ResearchIterations] ON;
                INSERT INTO [ResearchIterations] ([Id], [AIConnectionId], [AiModelUsed], [AiProviderUsed], [BaselineBenchmarkRunId], [CreatedAt], [EndedAt], [ExperimentId], [Hints], [LastMessage], [MaxNumberOfHypotheses], [ModifiedAt], [RegisteredBaseTables], [SchemaDiscoveryMarkdown], [SchemaDiscoveryResultJson], [StartedAt], [State])
                SELECT [Id], [AIConnectionId], [AiModelUsed], [AiProviderUsed], [BaselineBenchmarkRunId], [CreatedAt], [EndedAt], [ExperimentId], [Hints], [LastMessage], [MaxNumberOfHypotheses], [ModifiedAt], [RegisteredBaseTables], [SchemaDiscoveryMarkdown], [SchemaDiscoveryResultJson], [StartedAt], [State]
                FROM [ResearchIterations_old];
                SET IDENTITY_INSERT [ResearchIterations] OFF;
                IF EXISTS (SELECT 1 FROM [ResearchIterations])
                BEGIN
                    DECLARE @ri int = (SELECT MAX([Id]) FROM [ResearchIterations]);
                    DBCC CHECKIDENT ('[ResearchIterations]', RESEED, @ri);
                END
                """);

            migrationBuilder.RenameTable(
                name: "RunQueue",
                newName: "RunQueue_old");

            migrationBuilder.CreateTable(
                name: "RunQueue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResearchIterationId = table.Column<int>(type: "int", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunQueue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunQueue_HypothesisBatches_HypothesisBatchId",
                        column: x => x.ResearchIterationId,
                        principalTable: "ResearchIterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunQueue_ResearchIterationId",
                table: "RunQueue",
                column: "ResearchIterationId",
                unique: true);

            migrationBuilder.Sql(
                """
                SET IDENTITY_INSERT [RunQueue] ON;
                INSERT INTO [RunQueue] ([Id], [CreatedAt], [ModifiedAt], [ResearchIterationId])
                SELECT [Id], [CreatedAt], [ModifiedAt], [ResearchIterationId]
                FROM [RunQueue_old];
                SET IDENTITY_INSERT [RunQueue] OFF;
                IF EXISTS (SELECT 1 FROM [RunQueue])
                BEGIN
                    DECLARE @rq int = (SELECT MAX([Id]) FROM [RunQueue]);
                    DBCC CHECKIDENT ('[RunQueue]', RESEED, @rq);
                END
                """);

            migrationBuilder.DropTable(name: "ResearchIterations_old");
            migrationBuilder.DropTable(name: "RunQueue_old");

            migrationBuilder.AddForeignKey(
                name: "FK_Hypotheses_HypothesisBatches_HypothesisBatchId",
                table: "Hypotheses",
                column: "ResearchIterationId",
                principalTable: "ResearchIterations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdAfter",
                table: "Hypotheses",
                column: "BenchmarkRunIdAfter",
                principalTable: "BenchmarkRuns",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Hypotheses_BenchmarkRuns_BenchmarkRunIdBefore",
                table: "Hypotheses",
                column: "BenchmarkRunIdBefore",
                principalTable: "BenchmarkRuns",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "Reverting EntityPrimaryKeyIdentity would strip IDENTITY from multiple tables; this migration is not reversible.");
        }
    }
}
