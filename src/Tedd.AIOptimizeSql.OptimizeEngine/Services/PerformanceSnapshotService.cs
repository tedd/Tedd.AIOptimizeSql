using System.Data.Common;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.Database.Models.Enums;
using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Services;

/// <summary>
/// Collects a deterministic performance snapshot from a SQL Server database
/// (read-only DMV queries) and derives rule-based findings from it: missing
/// indexes, fragmentation, stale statistics, unused indexes, configuration
/// problems — plus positive findings for things that are healthy.
/// </summary>
public sealed class PerformanceSnapshotService(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PerformanceSnapshotService>();

    public const string SectionMissingIndexes = "MissingIndexes";
    public const string SectionIndexFragmentation = "IndexFragmentation";
    public const string SectionIndexUsage = "IndexUsage";
    public const string SectionStatisticsHealth = "StatisticsHealth";
    public const string SectionTopQueriesByCpu = "TopQueriesByCpu";
    public const string SectionTopQueriesByReads = "TopQueriesByReads";
    public const string SectionStoredProcedureStats = "StoredProcedureStats";
    public const string SectionWaitStatistics = "WaitStatistics";
    public const string SectionTableSizes = "TableSizes";
    public const string SectionDatabaseConfiguration = "DatabaseConfiguration";
    public const string SectionScopedConfiguration = "ScopedConfiguration";
    public const string SectionProceduresAndViews = "ProceduresAndViews";

    private static readonly (string Name, string Sql)[] Collectors =
    [
        (SectionMissingIndexes, PerformanceMetricsToolWrapper.Queries.MissingIndexes),
        (SectionIndexFragmentation, PerformanceMetricsToolWrapper.Queries.IndexFragmentation),
        (SectionIndexUsage, PerformanceMetricsToolWrapper.Queries.IndexUsage),
        (SectionStatisticsHealth, PerformanceMetricsToolWrapper.Queries.StatisticsHealth),
        (SectionTopQueriesByCpu, PerformanceMetricsToolWrapper.Queries.TopQueriesByCpu),
        (SectionTopQueriesByReads, PerformanceMetricsToolWrapper.Queries.TopQueriesByReads),
        (SectionStoredProcedureStats, PerformanceMetricsToolWrapper.Queries.StoredProcedureStats),
        (SectionWaitStatistics, PerformanceMetricsToolWrapper.Queries.WaitStatistics),
        (SectionTableSizes, PerformanceMetricsToolWrapper.Queries.TableSizes),
        (SectionDatabaseConfiguration, PerformanceMetricsToolWrapper.Queries.DatabaseConfiguration),
        (SectionScopedConfiguration, PerformanceMetricsToolWrapper.Queries.ScopedConfiguration),
        (SectionProceduresAndViews, PerformanceMetricsToolWrapper.Queries.ProceduresAndViews),
    ];

    /// <summary>
    /// Runs all collectors against the open connection. Individual collector
    /// failures (e.g. missing VIEW SERVER STATE permission) are recorded in
    /// <see cref="PerformanceSnapshot.Errors"/> and do not abort the snapshot.
    /// </summary>
    public PerformanceSnapshot Collect(
        IDatabaseExecutor executor,
        DbConnection connection,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new PerformanceSnapshot();

        try
        {
            snapshot.DatabaseName = executor.ExecuteScalar(connection, "SELECT DB_NAME()");
            snapshot.ServerVersion = executor.ExecuteScalar(connection, "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ProductVersion')) + ' ' + CONVERT(nvarchar(256), SERVERPROPERTY('Edition'))");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read database name / version");
        }

        foreach (var (name, sql) in Collectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"Collecting {name}...");
            try
            {
                snapshot.Sections[name] = executor.ExecuteQuery(connection, sql);
                _logger.LogDebug("Collector {Name}: {Rows} rows", name, snapshot.Sections[name].Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Collector {Name} failed", name);
                snapshot.Errors[name] = ex.Message;
            }
        }

        return snapshot;
    }

    #region Markdown summary

    /// <summary>
    /// Builds a compact markdown summary of the snapshot for the AI prompt and the UI.
    /// </summary>
    public static string BuildMarkdownSummary(PerformanceSnapshot snapshot, int maxRowsPerSection = 15)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Database: **{snapshot.DatabaseName ?? "(unknown)"}**, SQL Server {snapshot.ServerVersion ?? "(unknown)"}, collected {snapshot.CollectedAtUtc:u}");
        sb.AppendLine();

        foreach (var (section, rows) in snapshot.Sections)
        {
            sb.AppendLine($"### {section} ({rows.Count} rows)");
            sb.AppendLine();

            if (rows.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                continue;
            }

            var columns = rows[0].Keys.ToList();
            sb.AppendLine("| " + string.Join(" | ", columns) + " |");
            sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", columns.Count)));
            foreach (var row in rows.Take(maxRowsPerSection))
                sb.AppendLine("| " + string.Join(" | ", columns.Select(c => Sanitize(row.GetValueOrDefault(c, "")))) + " |");

            if (rows.Count > maxRowsPerSection)
                sb.AppendLine($"| ... {rows.Count - maxRowsPerSection} more rows ... |");

            sb.AppendLine();
        }

        foreach (var (section, error) in snapshot.Errors)
            sb.AppendLine($"> Collector **{section}** failed: {error}");

        return sb.ToString();

        static string Sanitize(string value)
        {
            var v = value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
            return v.Length > 200 ? v[..200] + "…" : v;
        }
    }

    #endregion

    #region Deterministic findings

    /// <summary>
    /// Derives rule-based findings from the snapshot. Returned findings have no
    /// <see cref="AnalysisFinding.DatabaseAnalysisId"/> set; the caller assigns it.
    /// </summary>
    public static List<AnalysisFinding> BuildDeterministicFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId)
    {
        var findings = new List<AnalysisFinding>();

        AddMissingIndexFindings(snapshot, analysisId, findings);
        AddFragmentationFindings(snapshot, analysisId, findings);
        AddStatisticsFindings(snapshot, analysisId, findings);
        AddUnusedIndexFindings(snapshot, analysisId, findings);
        AddWaitStatsFindings(snapshot, analysisId, findings);
        AddConfigurationFindings(snapshot, analysisId, findings);

        return findings;
    }

    private static void AddMissingIndexFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionMissingIndexes, out var rows))
            return;

        var relevant = rows
            .Select(r => (Row: r, Measure: ParseDouble(r.GetValueOrDefault("improvement_measure"))))
            .Where(x => x.Measure >= 10_000)
            .OrderByDescending(x => x.Measure)
            .Take(15)
            .ToList();

        if (relevant.Count == 0)
        {
            findings.Add(NewFinding(analysisId, FindingCategory.MissingIndex, FindingSeverity.Good,
                "No significant missing indexes recorded",
                "SQL Server's missing index DMVs show no high-impact missing index suggestions since the last restart. " +
                "The workload's access paths appear to be covered by existing indexes.",
                source: "Collector"));
            return;
        }

        foreach (var (row, measure) in relevant)
        {
            var schema = row.GetValueOrDefault("schema_name", "dbo");
            var table = row.GetValueOrDefault("table_name", "?");
            var equality = NullToEmpty(row.GetValueOrDefault("equality_columns"));
            var inequality = NullToEmpty(row.GetValueOrDefault("inequality_columns"));
            var included = NullToEmpty(row.GetValueOrDefault("included_columns"));
            var seeks = row.GetValueOrDefault("user_seeks", "0");
            var impact = row.GetValueOrDefault("avg_user_impact_pct", "?");

            var severity = measure switch
            {
                >= 1_000_000 => FindingSeverity.High,
                >= 100_000 => FindingSeverity.Medium,
                _ => FindingSeverity.Low,
            };

            var keyColumns = string.Join(", ", new[] { equality, inequality }.Where(s => s.Length > 0));
            var indexName = SuggestIndexName(table, equality.Length > 0 ? equality : inequality);
            var createSql = new StringBuilder();
            createSql.Append($"CREATE NONCLUSTERED INDEX [{indexName}]\n    ON [{schema}].[{table}] ({keyColumns})");
            if (included.Length > 0)
                createSql.Append($"\n    INCLUDE ({included})");
            createSql.Append(";\n");
            createSql.Append($"-- Revert: DROP INDEX [{indexName}] ON [{schema}].[{table}];");

            findings.Add(NewFinding(analysisId, FindingCategory.MissingIndex, severity,
                $"Missing index on {schema}.{table} ({FormatColumnsShort(keyColumns)})",
                $"The query optimizer recorded {seeks} seeks that would have used this index, with an average expected " +
                $"impact of {impact}% on query cost. Improvement measure: {measure:N0} (cost × impact × uses).",
                evidence:
                    $"- Equality columns: {(equality.Length > 0 ? equality : "—")}\n" +
                    $"- Inequality columns: {(inequality.Length > 0 ? inequality : "—")}\n" +
                    $"- Included columns: {(included.Length > 0 ? included : "—")}\n" +
                    $"- Last user seek: {row.GetValueOrDefault("last_user_seek", "—")}",
                recommendation:
                    "Consider creating the index below. Verify against existing similar indexes first (a wider existing index " +
                    "may be extendable instead), and test write-workload impact before applying to production.",
                recommendationSql: createSql.ToString(),
                objectSchema: schema, objectName: table,
                impactScore: measure,
                source: "Collector"));
        }
    }

    private static void AddFragmentationFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionIndexFragmentation, out var rows))
            return;

        var problematic = rows
            .Select(r => (Row: r,
                Frag: ParseDouble(r.GetValueOrDefault("avg_fragmentation_pct")),
                Pages: ParseLong(r.GetValueOrDefault("page_count"))))
            .Where(x => x.Frag >= 30 && x.Pages >= 1_000)
            .OrderByDescending(x => x.Frag * x.Pages)
            .Take(15)
            .ToList();

        if (problematic.Count == 0)
        {
            findings.Add(NewFinding(analysisId, FindingCategory.IndexFragmentation, FindingSeverity.Good,
                "No problematic index fragmentation",
                "No index with at least 1,000 pages is fragmented above 30%. Index maintenance appears adequate.",
                source: "Collector"));
            return;
        }

        foreach (var (row, frag, pages) in problematic)
        {
            var schema = row.GetValueOrDefault("schema_name", "dbo");
            var table = row.GetValueOrDefault("table_name", "?");
            var index = row.GetValueOrDefault("index_name", "?");

            var severity = pages >= 100_000 ? FindingSeverity.High : FindingSeverity.Medium;

            findings.Add(NewFinding(analysisId, FindingCategory.IndexFragmentation, severity,
                $"Index {index} on {schema}.{table} is {frag:0.#}% fragmented",
                $"The index spans {pages:N0} pages with {frag:0.#}% logical fragmentation. High fragmentation causes extra " +
                "I/O on range scans and wastes buffer pool memory.",
                evidence: $"- Index type: {row.GetValueOrDefault("index_type_desc", "?")}\n" +
                          $"- Pages: {pages:N0}\n" +
                          $"- Fragmentation: {frag:0.##}%",
                recommendation: "Rebuild the index (preferably ONLINE if the edition supports it) during a maintenance window, " +
                                "and consider a regular index maintenance job if fragmentation recurs quickly.",
                recommendationSql:
                    $"ALTER INDEX [{index}] ON [{schema}].[{table}] REBUILD WITH (ONLINE = ON);\n" +
                    $"-- If ONLINE is not available: ALTER INDEX [{index}] ON [{schema}].[{table}] REBUILD;",
                objectSchema: schema, objectName: table,
                impactScore: frag * pages,
                source: "Collector"));
        }
    }

    private static void AddStatisticsFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionStatisticsHealth, out var rows))
            return;

        var stale = rows
            .Select(r => (Row: r,
                Rows: ParseLong(r.GetValueOrDefault("rows")),
                Mods: ParseLong(r.GetValueOrDefault("modification_counter"))))
            .Where(x => x.Rows >= 10_000 && x.Mods > 0 && (double)x.Mods / x.Rows >= 0.20)
            .OrderByDescending(x => (double)x.Mods / x.Rows)
            .Take(15)
            .ToList();

        if (stale.Count == 0)
        {
            findings.Add(NewFinding(analysisId, FindingCategory.OutdatedStatistics, FindingSeverity.Good,
                "Statistics are reasonably fresh",
                "No statistics object on a significant table (≥10,000 rows) has accumulated more than 20% row modifications " +
                "since its last update.",
                source: "Collector"));
            return;
        }

        foreach (var (row, rowCount, mods) in stale)
        {
            var schema = row.GetValueOrDefault("schema_name", "dbo");
            var table = row.GetValueOrDefault("table_name", "?");
            var stats = row.GetValueOrDefault("stats_name", "?");
            var pct = rowCount > 0 ? 100.0 * mods / rowCount : 0;

            findings.Add(NewFinding(analysisId, FindingCategory.OutdatedStatistics,
                pct >= 100 ? FindingSeverity.High : FindingSeverity.Medium,
                $"Stale statistics {stats} on {schema}.{table} ({pct:0}% modified)",
                $"Statistics object '{stats}' has seen {mods:N0} modifications against {rowCount:N0} rows " +
                $"({pct:0.#}%) since its last update ({row.GetValueOrDefault("last_updated", "unknown")}). " +
                "Outdated statistics lead to poor cardinality estimates and bad plans.",
                evidence: $"- Rows: {rowCount:N0}\n- Modifications since update: {mods:N0}\n" +
                          $"- Last updated: {row.GetValueOrDefault("last_updated", "—")}\n" +
                          $"- Sample: {row.GetValueOrDefault("sample_pct", "—")}%",
                recommendation: "Update the statistics (consider FULLSCAN for skewed or critical tables) and verify that " +
                                "auto-update statistics is enabled or a maintenance job refreshes them regularly.",
                recommendationSql: $"UPDATE STATISTICS [{schema}].[{table}] [{stats}] WITH FULLSCAN;",
                objectSchema: schema, objectName: table,
                impactScore: pct,
                source: "Collector"));
        }
    }

    private static void AddUnusedIndexFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionIndexUsage, out var rows))
            return;

        var unused = rows
            .Select(r => (Row: r,
                Reads: ParseLong(r.GetValueOrDefault("user_seeks")) + ParseLong(r.GetValueOrDefault("user_scans")) + ParseLong(r.GetValueOrDefault("user_lookups")),
                Updates: ParseLong(r.GetValueOrDefault("user_updates"))))
            .Where(x => x.Reads == 0 && x.Updates >= 1_000
                        && !IsTrue(x.Row.GetValueOrDefault("is_primary_key"))
                        && !IsTrue(x.Row.GetValueOrDefault("is_unique")))
            .OrderByDescending(x => x.Updates)
            .Take(10)
            .ToList();

        foreach (var (row, _, updates) in unused)
        {
            var schema = row.GetValueOrDefault("schema_name", "dbo");
            var table = row.GetValueOrDefault("table_name", "?");
            var index = row.GetValueOrDefault("index_name", "?");

            findings.Add(NewFinding(analysisId, FindingCategory.UnusedIndex, FindingSeverity.Medium,
                $"Unused index {index} on {schema}.{table} ({updates:N0} wasted writes)",
                $"Since the last restart this index has never been read (0 seeks/scans/lookups) but was maintained through " +
                $"{updates:N0} write operations. It costs write performance and storage without benefiting any query.",
                evidence: $"- Seeks/scans/lookups: 0\n- Updates: {updates:N0}\n- Type: {row.GetValueOrDefault("type_desc", "?")}",
                recommendation: "Verify usage over a full business cycle (usage stats reset on restart, and rare " +
                                "month-end/reporting queries may still need it), then consider dropping the index.",
                recommendationSql: $"DROP INDEX [{index}] ON [{schema}].[{table}];\n-- Keep the CREATE statement handy to restore it if needed.",
                objectSchema: schema, objectName: table,
                impactScore: updates,
                source: "Collector"));
        }
    }

    private static void AddWaitStatsFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionWaitStatistics, out var rows) || rows.Count == 0)
            return;

        var top = rows[0];
        var pct = ParseDouble(top.GetValueOrDefault("pct_of_total"));
        var waitType = top.GetValueOrDefault("wait_type", "?");

        if (pct >= 40)
        {
            findings.Add(NewFinding(analysisId, FindingCategory.WaitStatistics, FindingSeverity.Info,
                $"Dominant wait type: {waitType} ({pct:0.#}% of wait time)",
                $"'{waitType}' accounts for {pct:0.#}% of all non-benign wait time since the last restart " +
                $"({top.GetValueOrDefault("wait_time_ms", "?")} ms across {top.GetValueOrDefault("waiting_tasks_count", "?")} waits). " +
                "The dominant wait type indicates where the server spends its time and which resource to tune first.",
                evidence: string.Join("\n", rows.Take(5).Select(r =>
                    $"- {r.GetValueOrDefault("wait_type")}: {r.GetValueOrDefault("wait_time_ms")} ms ({r.GetValueOrDefault("pct_of_total")}%)")),
                recommendation: "Interpret the wait profile: PAGEIOLATCH_* = storage/buffer pool pressure, CXPACKET/CXCONSUMER = " +
                                "parallelism, LCK_* = blocking, SOS_SCHEDULER_YIELD = CPU pressure, WRITELOG = log latency.",
                source: "Collector"));
        }
    }

    private static void AddConfigurationFindings(PerformanceSnapshot snapshot, DatabaseAnalysisId analysisId, List<AnalysisFinding> findings)
    {
        if (!snapshot.Sections.TryGetValue(SectionDatabaseConfiguration, out var rows) || rows.Count == 0)
            return;

        var cfg = rows[0];
        var dbName = cfg.GetValueOrDefault("database_name", "the database");

        void Add(FindingSeverity severity, string title, string description, string? recommendation = null, string? sql = null) =>
            findings.Add(NewFinding(analysisId, FindingCategory.Configuration, severity, title, description,
                recommendation: recommendation, recommendationSql: sql, source: "Collector"));

        if (IsTrue(cfg.GetValueOrDefault("is_auto_shrink_on")))
            Add(FindingSeverity.High, "AUTO_SHRINK is enabled",
                "Auto-shrink periodically shrinks and regrows the database, causing massive fragmentation and I/O spikes. It should be off in virtually every production scenario.",
                "Disable auto-shrink.", $"ALTER DATABASE [{dbName}] SET AUTO_SHRINK OFF;");

        if (IsTrue(cfg.GetValueOrDefault("is_auto_close_on")))
            Add(FindingSeverity.High, "AUTO_CLOSE is enabled",
                "Auto-close shuts the database down when the last user disconnects; every reconnect pays full startup cost and flushes caches.",
                "Disable auto-close.", $"ALTER DATABASE [{dbName}] SET AUTO_CLOSE OFF;");

        if (!IsTrue(cfg.GetValueOrDefault("is_auto_create_stats_on")))
            Add(FindingSeverity.Medium, "Auto-create statistics is disabled",
                "Without auto-created statistics the optimizer lacks cardinality information on non-indexed columns, producing poor plans.",
                "Enable auto-create statistics unless a deliberate strategy replaces it.",
                $"ALTER DATABASE [{dbName}] SET AUTO_CREATE_STATISTICS ON;");

        if (!IsTrue(cfg.GetValueOrDefault("is_auto_update_stats_on")))
            Add(FindingSeverity.Medium, "Auto-update statistics is disabled",
                "Statistics silently go stale as data changes, degrading plan quality over time.",
                "Enable auto-update statistics unless a maintenance job handles it.",
                $"ALTER DATABASE [{dbName}] SET AUTO_UPDATE_STATISTICS ON;");

        var pageVerify = cfg.GetValueOrDefault("page_verify_option_desc", "");
        if (!string.Equals(pageVerify, "CHECKSUM", StringComparison.OrdinalIgnoreCase) && pageVerify.Length > 0)
            Add(FindingSeverity.Medium, $"Page verify option is {pageVerify}, not CHECKSUM",
                "CHECKSUM page verification detects storage corruption early with negligible overhead.",
                "Switch page verification to CHECKSUM.",
                $"ALTER DATABASE [{dbName}] SET PAGE_VERIFY CHECKSUM;");

        if (!IsTrue(cfg.GetValueOrDefault("is_query_store_on")))
            Add(FindingSeverity.Low, "Query Store is not enabled",
                "Query Store records query performance history and enables plan regression detection and forced plans — invaluable for performance work.",
                "Enable Query Store in read-write mode.",
                $"ALTER DATABASE [{dbName}] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE);");
        else
            Add(FindingSeverity.Good, "Query Store is enabled",
                "Query Store is on, providing query performance history and plan-regression tooling.");

        if (IsTrue(cfg.GetValueOrDefault("is_read_committed_snapshot_on")))
            Add(FindingSeverity.Good, "Read Committed Snapshot Isolation (RCSI) is enabled",
                "Readers do not block writers and vice versa, which greatly reduces blocking in mixed workloads.");

        if (IsTrue(cfg.GetValueOrDefault("is_auto_create_stats_on")) && IsTrue(cfg.GetValueOrDefault("is_auto_update_stats_on")))
            Add(FindingSeverity.Good, "Automatic statistics management is enabled",
                "Both auto-create and auto-update statistics are on; the optimizer keeps cardinality data up to date on its own.");
    }

    #endregion

    #region Helpers

    private static AnalysisFinding NewFinding(
        DatabaseAnalysisId analysisId,
        FindingCategory category, FindingSeverity severity,
        string title, string description,
        string? evidence = null, string? recommendation = null, string? recommendationSql = null,
        string? objectSchema = null, string? objectName = null,
        double impactScore = 0, string? source = null)
    {
        var now = DateTime.UtcNow;
        return new AnalysisFinding
        {
            Id = AnalysisFindingId.Transient,
            DatabaseAnalysisId = analysisId,
            Category = category,
            Severity = severity,
            Title = title.Length > 1024 ? title[..1024] : title,
            Description = description,
            Evidence = evidence,
            Recommendation = recommendation,
            RecommendationSql = recommendationSql,
            ObjectSchema = objectSchema,
            ObjectName = objectName,
            ImpactScore = impactScore,
            Source = source,
            CreatedAt = now,
            ModifiedAt = now,
        };
    }

    private static string SuggestIndexName(string table, string columns)
    {
        var cols = columns
            .Replace("[", "").Replace("]", "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(3);
        var name = $"IX_{table}_{string.Join("_", cols)}".Replace(" ", "");
        return name.Length > 120 ? name[..120] : name;
    }

    private static string FormatColumnsShort(string columns)
    {
        var clean = columns.Replace("[", "").Replace("]", "");
        return clean.Length > 60 ? clean[..60] + "…" : clean;
    }

    private static string NullToEmpty(string? value) =>
        value is null || value == "NULL" ? "" : value;

    internal static double ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "NULL")
            return 0;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out d))
            return d;
        return 0;
    }

    internal static long ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "NULL")
            return 0;
        if (long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var l))
            return l;
        // Values such as "1234.0" or culture-formatted numbers
        return (long)ParseDouble(value);
    }

    internal static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1");

    #endregion
}
