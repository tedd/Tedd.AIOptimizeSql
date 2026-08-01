using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Models.SqlBrowser;
using Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

namespace Tedd.AIOptimizeSql.Tests;

/// <summary>
/// Covers the script assembly, which is where a sandbox quietly becomes wrong: a missing GO, a
/// forgotten IDENTITY_INSERT, or a foreign key still pointing at the production table.
/// </summary>
public class SandboxScriptServiceTests
{
    #region Fixtures

    private static TableColumnDefinition Column(
        string name, string type = "int", bool nullable = false, bool identity = false,
        bool computed = false, string? computedDefinition = null) =>
        new(
            Name: name,
            TypeName: type,
            TypeSchema: "sys",
            IsUserDefinedType: false,
            MaxLength: 4,
            Precision: 10,
            Scale: 0,
            IsNullable: nullable,
            IsIdentity: identity,
            IdentitySeed: identity ? "1" : null,
            IdentityIncrement: identity ? "1" : null,
            IsComputed: computed,
            ComputedDefinition: computedDefinition,
            IsPersisted: false,
            DefaultName: null,
            DefaultDefinition: null,
            CollationName: null,
            DatabaseCollation: null);

    private static TableDefinition Customers() => new()
    {
        Schema = "dbo",
        Table = "Customers",
        Columns = [Column("CustomerId", identity: true), Column("Name", "nvarchar")],
        Keys = [new TableKeyDefinition("PK_Customers", "PK", "CLUSTERED", [new TableIndexColumn("CustomerId", false)])],
        Indexes = [],
        ForeignKeys = []
    };

    private static TableDefinition Orders() => new()
    {
        Schema = "dbo",
        Table = "Orders",
        Columns =
        [
            Column("OrderId", identity: true),
            Column("CustomerId"),
            Column("Total", "decimal"),
            Column("TotalWithTax", computed: true, computedDefinition: "([Total]*1.25)")
        ],
        Keys = [new TableKeyDefinition("PK_Orders", "PK", "CLUSTERED", [new TableIndexColumn("OrderId", false)])],
        Indexes =
        [
            new TableIndexDefinition(2, "IX_Orders_CustomerId", 2, "NONCLUSTERED", false, null,
                [new TableIndexColumn("CustomerId", false)], ["Total"])
        ],
        ForeignKeys =
        [
            new TableForeignKeyDefinition("FK_Orders_Customers", "dbo", "Customers",
                ["CustomerId"], ["CustomerId"], "NO_ACTION", "NO_ACTION", false, false)
        ]
    };

    private static SandboxScriptService.SandboxModel Model(
        IEnumerable<TableDefinition>? tables = null,
        IEnumerable<SandboxScriptService.ModuleDefinition>? modules = null,
        int existingObjects = 0) => new()
        {
            SourceDatabase = "SalesDb",
            Tables = (tables ?? [Customers(), Orders()]).ToList(),
            Modules = (modules ?? []).ToList(),
            Warnings = [],
            SchemaContextMarkdown = "",
            ExistingObjectsInSandboxSchema = existingObjects
        };

    #endregion

    #region Clone database

    [Fact]
    public void CloneSetup_CreatesTheDatabaseAndBatchesBeforeUsingThreePartNames()
    {
        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", Model());

        Assert.Contains("CREATE DATABASE [SalesDb_clone];", setup);
        // Three-part names bind when the batch compiles, so the clone has to exist by then.
        var createIndex = setup.IndexOf("CREATE DATABASE [SalesDb_clone];", StringComparison.Ordinal);
        var firstThreePart = setup.IndexOf("[SalesDb_clone].[dbo].", StringComparison.Ordinal);
        var goBetween = setup.IndexOf("\nGO", createIndex, StringComparison.Ordinal);
        Assert.True(goBetween > createIndex && goBetween < firstThreePart,
            "a GO must separate CREATE DATABASE from the first three-part name");
    }

    [Fact]
    public void CloneSetup_RunsDdlInsideTheCloneRatherThanMaster()
    {
        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", Model());

        Assert.Contains("EXEC [SalesDb_clone].sys.sp_executesql N'CREATE TABLE [dbo].[Orders]", setup);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId]", setup);
    }

    [Fact]
    public void CloneSetup_CopiesRowsWithIdentityInsertAndSkipsComputedColumns()
    {
        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", Model());

        Assert.Contains("SET IDENTITY_INSERT [SalesDb_clone].[dbo].[Orders] ON;", setup);
        Assert.Contains("SET IDENTITY_INSERT [SalesDb_clone].[dbo].[Orders] OFF;", setup);
        Assert.Contains(
            "INSERT INTO [SalesDb_clone].[dbo].[Orders] ([OrderId], [CustomerId], [Total])",
            setup);
        Assert.Contains("SELECT [OrderId], [CustomerId], [Total] FROM [SalesDb].[dbo].[Orders];", setup);
        // A computed column is regenerated by its own definition; inserting into it fails.
        Assert.DoesNotContain("[TotalWithTax] FROM", setup);
    }

    [Fact]
    public void CloneSetup_AddsForeignKeysOnlyAfterEveryTableIsLoaded()
    {
        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", Model());

        var lastInsert = setup.LastIndexOf("INSERT INTO", StringComparison.Ordinal);
        var foreignKey = setup.IndexOf("ADD CONSTRAINT [FK_Orders_Customers]", StringComparison.Ordinal);
        Assert.True(foreignKey > lastInsert, "foreign keys must be added after the data copy");
    }

    [Fact]
    public void CloneSetup_DropsAndRecreatesEachModuleInDependencyOrder()
    {
        var model = Model(modules:
        [
            new SandboxScriptService.ModuleDefinition("dbo", "vOrders", SqlObjectKind.View,
                "CREATE VIEW [dbo].[vOrders] AS SELECT * FROM [dbo].[Orders] WHERE [Total] > 0;")
        ]);

        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", model);

        Assert.Contains("DROP VIEW [dbo].[vOrders];", setup);
        Assert.Contains("CREATE VIEW [dbo].[vOrders] AS SELECT * FROM [dbo].[Orders] WHERE [Total] > 0;", setup);
    }

    [Fact]
    public void CloneSetup_SurvivesAModuleWhoseBodyContainsAStandaloneGoLine()
    {
        var definition = "CREATE PROCEDURE [dbo].[p] AS\n-- old version:\nGO\nSELECT 1;";
        var model = Model(modules:
        [
            new SandboxScriptService.ModuleDefinition("dbo", "p", SqlObjectKind.StoredProcedure, definition)
        ]);

        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", model);

        // The generated script is split on GO before it runs, so the line is carried across as a
        // placeholder and put back at run time instead of cutting the procedure in half.
        Assert.Contains("REPLACE(N'", setup);
        Assert.Contains("N'~~aiopt~batch~separator~~', N'GO'", setup);
        Assert.DoesNotContain("-- old version:\nGO\nSELECT 1;", setup);
    }

    [Fact]
    public void CloneSetup_EscapesQuotesInEmbeddedStatements()
    {
        var model = Model(modules:
        [
            new SandboxScriptService.ModuleDefinition("dbo", "v", SqlObjectKind.View,
                "CREATE VIEW [dbo].[v] AS SELECT 'a' AS [x];")
        ]);

        var (setup, _) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", model);

        Assert.Contains("SELECT ''a'' AS [x];", setup);
    }

    [Fact]
    public void CloneTeardown_DropsTheCloneAndIsSafeWhenSetupNeverRan()
    {
        var (_, teardown) = SandboxScriptService.BuildCloneDatabaseScripts("SalesDb_clone", Model());

        Assert.Contains("IF DB_ID(N'SalesDb_clone') IS NOT NULL", teardown);
        Assert.Contains("SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", teardown);
        Assert.Contains("DROP DATABASE [SalesDb_clone];", teardown);
    }

    #endregion

    #region Sandbox schema

    [Fact]
    public void SchemaSetup_CreatesTheTablesInTheSandboxSchemaAndReadsFromTheOriginals()
    {
        var (setup, _) = SandboxScriptService.BuildSandboxSchemaScripts("sbx", Model());

        Assert.Contains("CREATE TABLE [sbx].[Orders] (", setup);
        Assert.Contains("INSERT INTO [sbx].[Orders] ([OrderId], [CustomerId], [Total])", setup);
        Assert.Contains("FROM [dbo].[Orders];", setup);
        Assert.Contains("UPDATE STATISTICS [sbx].[Orders] WITH FULLSCAN;", setup);
    }

    [Fact]
    public void SchemaSetup_PointsForeignKeysAtTheSandboxCopyNotTheOriginal()
    {
        var (setup, _) = SandboxScriptService.BuildSandboxSchemaScripts("sbx", Model());

        Assert.Contains("ALTER TABLE [sbx].[Orders]", setup);
        Assert.Contains("REFERENCES [sbx].[Customers] ([CustomerId])", setup);
        Assert.DoesNotContain("REFERENCES [dbo].[Customers]", setup);
    }

    [Fact]
    public void SchemaSetup_DropsAForeignKeyWhoseParentIsNotCopied_AndSaysSo()
    {
        // Orders is copied but Customers is not, so the key has nothing to point at inside the
        // sandbox — and pointing it back at production would let a sandbox row depend on a real one.
        var model = Model(tables: [Orders()]);

        var (setup, _) = SandboxScriptService.BuildSandboxSchemaScripts("sbx", model);

        Assert.DoesNotContain("FK_Orders_Customers", setup);
        Assert.Contains(model.Warnings, w => w.Contains("[FK_Orders_Customers]") && w.Contains("not copied"));
    }

    [Fact]
    public void SchemaSetup_WarnsWhenTwoSourceSchemasWouldCollideInTheFlatSandbox()
    {
        var other = Customers() with { Schema = "archive" };
        var model = Model(tables: [Customers(), other]);

        SandboxScriptService.BuildSandboxSchemaScripts("sbx", model);

        Assert.Contains(model.Warnings, w => w.Contains("More than one source schema has a table called [Customers]"));
    }

    [Fact]
    public void SchemaTeardown_RemovesEverythingInTheSchemaThenTheSchema()
    {
        var (_, teardown) = SandboxScriptService.BuildSandboxSchemaScripts("sbx", Model());

        var foreignKeys = teardown.IndexOf("FROM sys.foreign_keys", StringComparison.Ordinal);
        var modules = teardown.IndexOf("FROM sys.objects", StringComparison.Ordinal);
        var tables = teardown.IndexOf("FROM sys.tables", StringComparison.Ordinal);
        var dropSchema = teardown.IndexOf("DROP SCHEMA [sbx];", StringComparison.Ordinal);

        Assert.True(foreignKeys < modules && modules < tables && tables < dropSchema,
            "teardown has to unwind in dependency order: keys, modules, tables, then the schema");
        Assert.Contains("IF SCHEMA_ID(N'sbx') IS NOT NULL", teardown);
    }

    [Fact]
    public void SchemaSetup_ClearsThePreviousRunSoItCanBeRunTwice()
    {
        var (setup, _) = SandboxScriptService.BuildSandboxSchemaScripts("sbx", Model());

        Assert.Contains("Clear anything a previous run left behind.", setup);
        // ...but the clean-slate block must not drop the schema it is about to fill.
        Assert.DoesNotContain("DROP SCHEMA [sbx];", setup);
        Assert.Contains("IF SCHEMA_ID(N'sbx') IS NULL", setup);
    }

    #endregion

    #region Refusals

    [Theory]
    [InlineData("dbo")]
    [InlineData("sys")]
    [InlineData("db_owner")]
    public void Refuse_RejectsABuiltInSchema(string schema)
    {
        var refusal = SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.SandboxSchema, SandboxSchemaName = schema },
            Model());

        Assert.NotNull(refusal);
        Assert.Contains("built-in schema", refusal);
    }

    [Fact]
    public void Refuse_RejectsASchemaThatAlreadyHoldsSomebodyElsesObjects()
    {
        var refusal = SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.SandboxSchema, SandboxSchemaName = "staging" },
            Model(existingObjects: 12));

        Assert.NotNull(refusal);
        Assert.Contains("12 object(s)", refusal);
    }

    [Fact]
    public void Refuse_RejectsACloneNamedAfterTheSourceOrASystemDatabase()
    {
        Assert.NotNull(SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.CloneDatabase, SandboxDatabaseName = "SalesDb" },
            Model()));

        Assert.NotNull(SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.CloneDatabase, SandboxDatabaseName = "msdb" },
            Model()));
    }

    [Fact]
    public void Refuse_AllowsADedicatedName()
    {
        Assert.Null(SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.CloneDatabase, SandboxDatabaseName = "SalesDb_clone" },
            Model()));

        Assert.Null(SandboxScriptService.Refuse(
            new SandboxScriptRequest { IsolationMode = ExperimentIsolationMode.SandboxSchema, SandboxSchemaName = "aiopt_sandbox" },
            Model()));
    }

    #endregion
}
