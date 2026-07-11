using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class PerformanceSnapshotServiceTests
{
    private static readonly DatabaseAnalysisId AnalysisId = (DatabaseAnalysisId)42;

    private static Dictionary<string, string> Row(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void BuildDeterministicFindings_MissingIndex_MapsSeverityByImprovementMeasure()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionMissingIndexes] = new()
        {
            Row(("schema_name", "dbo"), ("table_name", "Orders"), ("improvement_measure", "2500000"),
                ("equality_columns", "[CustomerId]"), ("inequality_columns", "NULL"), ("included_columns", "[OrderDate]"),
                ("user_seeks", "1000"), ("avg_user_impact_pct", "85.5")),
            Row(("schema_name", "dbo"), ("table_name", "Lines"), ("improvement_measure", "150000"),
                ("equality_columns", "[OrderId]"), ("inequality_columns", "NULL"), ("included_columns", "NULL"),
                ("user_seeks", "50"), ("avg_user_impact_pct", "40")),
            Row(("schema_name", "dbo"), ("table_name", "Tiny"), ("improvement_measure", "500"),
                ("equality_columns", "[x]"), ("inequality_columns", "NULL"), ("included_columns", "NULL"),
                ("user_seeks", "1"), ("avg_user_impact_pct", "1")),
        };

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var missing = findings.Where(f => f.Category == FindingCategory.MissingIndex).ToList();

        Assert.Equal(2, missing.Count); // 500 is below the 10k threshold
        Assert.Equal(FindingSeverity.High, missing[0].Severity);
        Assert.Equal(FindingSeverity.Medium, missing[1].Severity);
        Assert.Contains("CREATE NONCLUSTERED INDEX", missing[0].RecommendationSql);
        Assert.Contains("[dbo].[Orders]", missing[0].RecommendationSql);
        Assert.Contains("INCLUDE ([OrderDate])", missing[0].RecommendationSql);
        Assert.Equal("Orders", missing[0].ObjectName);
    }

    [Fact]
    public void BuildDeterministicFindings_NoMissingIndexes_ProducesGoodFinding()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionMissingIndexes] = new();

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var good = findings.Single(f => f.Category == FindingCategory.MissingIndex);

        Assert.Equal(FindingSeverity.Good, good.Severity);
    }

    [Fact]
    public void BuildDeterministicFindings_Fragmentation_FlagsOnlyLargeFragmentedIndexes()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionIndexFragmentation] = new()
        {
            Row(("schema_name", "dbo"), ("table_name", "Big"), ("index_name", "IX_Big"),
                ("index_type_desc", "NONCLUSTERED"), ("avg_fragmentation_pct", "85.5"), ("page_count", "250000")),
            Row(("schema_name", "dbo"), ("table_name", "Mid"), ("index_name", "IX_Mid"),
                ("index_type_desc", "NONCLUSTERED"), ("avg_fragmentation_pct", "45"), ("page_count", "5000")),
            Row(("schema_name", "dbo"), ("table_name", "Small"), ("index_name", "IX_Small"),
                ("index_type_desc", "NONCLUSTERED"), ("avg_fragmentation_pct", "90"), ("page_count", "200")),
        };

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var frag = findings.Where(f => f.Category == FindingCategory.IndexFragmentation && f.Severity != FindingSeverity.Good).ToList();

        Assert.Equal(2, frag.Count); // small index ignored despite 90% fragmentation
        Assert.Equal(FindingSeverity.High, frag[0].Severity); // 250k pages
        Assert.Equal(FindingSeverity.Medium, frag[1].Severity);
        Assert.Contains("ALTER INDEX [IX_Big]", frag[0].RecommendationSql);
    }

    [Fact]
    public void BuildDeterministicFindings_Configuration_FlagsAutoShrinkAndRewardsQueryStore()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionDatabaseConfiguration] = new()
        {
            Row(("database_name", "Prod"),
                ("is_auto_shrink_on", "True"), ("is_auto_close_on", "False"),
                ("is_auto_create_stats_on", "True"), ("is_auto_update_stats_on", "True"),
                ("page_verify_option_desc", "CHECKSUM"),
                ("is_query_store_on", "True"),
                ("is_read_committed_snapshot_on", "False")),
        };

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var config = findings.Where(f => f.Category == FindingCategory.Configuration).ToList();

        Assert.Contains(config, f => f.Severity == FindingSeverity.High && f.Title.Contains("AUTO_SHRINK"));
        Assert.Contains(config, f => f.Severity == FindingSeverity.Good && f.Title.Contains("Query Store"));
        Assert.Contains(config, f => f.Severity == FindingSeverity.Good && f.Title.Contains("statistics"));
        Assert.DoesNotContain(config, f => f.Title.Contains("AUTO_CLOSE"));
    }

    [Fact]
    public void BuildDeterministicFindings_StaleStatistics_UsesModificationRatio()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionStatisticsHealth] = new()
        {
            Row(("schema_name", "dbo"), ("table_name", "Orders"), ("stats_name", "S1"),
                ("rows", "100000"), ("modification_counter", "150000"),
                ("last_updated", "2026-01-01"), ("sample_pct", "12.5")),
            Row(("schema_name", "dbo"), ("table_name", "Fresh"), ("stats_name", "S2"),
                ("rows", "100000"), ("modification_counter", "10"),
                ("last_updated", "2026-07-01"), ("sample_pct", "100")),
        };

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var stale = findings.Where(f => f.Category == FindingCategory.OutdatedStatistics && f.Severity != FindingSeverity.Good).ToList();

        Assert.Single(stale);
        Assert.Equal(FindingSeverity.High, stale[0].Severity); // 150% modified
        Assert.Contains("UPDATE STATISTICS [dbo].[Orders]", stale[0].RecommendationSql);
    }

    [Fact]
    public void BuildDeterministicFindings_UnusedIndex_SkipsPrimaryKeysAndUniqueIndexes()
    {
        var snapshot = new PerformanceSnapshot();
        snapshot.Sections[PerformanceSnapshotService.SectionIndexUsage] = new()
        {
            Row(("schema_name", "dbo"), ("table_name", "T"), ("index_name", "IX_Unused"),
                ("type_desc", "NONCLUSTERED"), ("is_unique", "False"), ("is_primary_key", "False"),
                ("user_seeks", "0"), ("user_scans", "0"), ("user_lookups", "0"), ("user_updates", "50000")),
            Row(("schema_name", "dbo"), ("table_name", "T"), ("index_name", "PK_T"),
                ("type_desc", "CLUSTERED"), ("is_unique", "True"), ("is_primary_key", "True"),
                ("user_seeks", "0"), ("user_scans", "0"), ("user_lookups", "0"), ("user_updates", "50000")),
        };

        var findings = PerformanceSnapshotService.BuildDeterministicFindings(snapshot, AnalysisId);
        var unused = findings.Where(f => f.Category == FindingCategory.UnusedIndex).ToList();

        Assert.Single(unused);
        Assert.Contains("IX_Unused", unused[0].Title);
    }

    [Fact]
    public void BuildMarkdownSummary_IncludesSectionsAndErrors()
    {
        var snapshot = new PerformanceSnapshot
        {
            DatabaseName = "TestDb",
            ServerVersion = "16.0 Enterprise",
        };
        snapshot.Sections["TableSizes"] = new()
        {
            Row(("schema_name", "dbo"), ("table_name", "T"), ("reserved_mb", "100")),
        };
        snapshot.Errors["WaitStatistics"] = "VIEW SERVER STATE permission denied";

        var md = PerformanceSnapshotService.BuildMarkdownSummary(snapshot);

        Assert.Contains("TestDb", md);
        Assert.Contains("TableSizes", md);
        Assert.Contains("| dbo | T | 100 |", md);
        Assert.Contains("VIEW SERVER STATE permission denied", md);
    }

    [Theory]
    [InlineData("1234", 1234L)]
    [InlineData("1234.9", 1234L)]
    [InlineData("NULL", 0L)]
    [InlineData("", 0L)]
    [InlineData(null, 0L)]
    public void ParseLong_HandlesVariedInput(string? input, long expected)
    {
        Assert.Equal(expected, PerformanceSnapshotService.ParseLong(input));
    }

    [Theory]
    [InlineData("85.5", 85.5)]
    [InlineData("NULL", 0)]
    [InlineData("garbage", 0)]
    public void ParseDouble_HandlesVariedInput(string? input, double expected)
    {
        Assert.Equal(expected, PerformanceSnapshotService.ParseDouble(input), precision: 3);
    }
}
