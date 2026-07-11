using System.ComponentModel;
using System.Data.Common;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Tedd.AIOptimizeSql.OptimizeEngine.Utils;

/// <summary>
/// SQL Server performance metric inspection tools exposed to AI agents via
/// <c>AIFunctionFactory.Create</c>. Every query here is a read-only DMV/catalog
/// query — safe to run against production databases in analyze-only mode.
/// </summary>
public sealed class PerformanceMetricsToolWrapper : IDisposable
{
    private readonly IDatabaseExecutor _executor;
    private readonly DbConnection _connection;
    private readonly int _maxResponseBytes;
    private readonly ILogger _logger;

    public PerformanceMetricsToolWrapper(
        IDatabaseExecutor executor, DbConnection connection,
        int maxResponseBytes, ILogger logger)
    {
        _executor = executor;
        _connection = connection;
        _maxResponseBytes = maxResponseBytes;
        _logger = logger;
    }

    /// <summary>
    /// SQL text for each metric query, keyed by a stable name. Shared with the
    /// deterministic snapshot collectors so AI tools and collectors agree.
    /// </summary>
    internal static class Queries
    {
        public const string MissingIndexes = """
            SELECT TOP 25
                DB_NAME(mid.database_id) AS database_name,
                OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id) AS schema_name,
                OBJECT_NAME(mid.object_id, mid.database_id) AS table_name,
                CONVERT(bigint, migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans)) AS improvement_measure,
                migs.user_seeks, migs.user_scans,
                CONVERT(decimal(18,2), migs.avg_total_user_cost) AS avg_total_user_cost,
                CONVERT(decimal(5,2), migs.avg_user_impact) AS avg_user_impact_pct,
                migs.last_user_seek,
                mid.equality_columns, mid.inequality_columns, mid.included_columns
            FROM sys.dm_db_missing_index_group_stats migs
            JOIN sys.dm_db_missing_index_groups mig ON migs.group_handle = mig.index_group_handle
            JOIN sys.dm_db_missing_index_details mid ON mig.index_handle = mid.index_handle
            WHERE mid.database_id = DB_ID()
            ORDER BY improvement_measure DESC
            """;

        public const string IndexFragmentation = """
            SELECT TOP 50
                OBJECT_SCHEMA_NAME(ips.object_id) AS schema_name,
                OBJECT_NAME(ips.object_id) AS table_name,
                i.name AS index_name,
                ips.index_type_desc,
                CONVERT(decimal(5,2), ips.avg_fragmentation_in_percent) AS avg_fragmentation_pct,
                ips.page_count,
                ips.fragment_count,
                CONVERT(decimal(5,2), ips.avg_page_space_used_in_percent) AS avg_page_space_used_pct
            FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
            JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
            WHERE ips.page_count >= 100
              AND ips.avg_fragmentation_in_percent >= 5
              AND i.name IS NOT NULL
            ORDER BY ips.avg_fragmentation_in_percent DESC
            """;

        public const string IndexUsage = """
            SELECT TOP 100
                OBJECT_SCHEMA_NAME(i.object_id) AS schema_name,
                OBJECT_NAME(i.object_id) AS table_name,
                i.name AS index_name,
                i.type_desc,
                i.is_unique, i.is_primary_key,
                ISNULL(ius.user_seeks, 0) AS user_seeks,
                ISNULL(ius.user_scans, 0) AS user_scans,
                ISNULL(ius.user_lookups, 0) AS user_lookups,
                ISNULL(ius.user_updates, 0) AS user_updates,
                ius.last_user_seek, ius.last_user_scan
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0
            LEFT JOIN sys.dm_db_index_usage_stats ius
                ON ius.object_id = i.object_id AND ius.index_id = i.index_id AND ius.database_id = DB_ID()
            WHERE i.name IS NOT NULL AND o.type = 'U'
            ORDER BY ISNULL(ius.user_seeks, 0) + ISNULL(ius.user_scans, 0) + ISNULL(ius.user_lookups, 0) ASC,
                     ISNULL(ius.user_updates, 0) DESC
            """;

        public const string StatisticsHealth = """
            SELECT TOP 100
                OBJECT_SCHEMA_NAME(s.object_id) AS schema_name,
                OBJECT_NAME(s.object_id) AS table_name,
                s.name AS stats_name,
                s.auto_created, s.user_created, s.no_recompute,
                sp.last_updated,
                sp.rows, sp.rows_sampled,
                CASE WHEN sp.rows > 0 THEN CONVERT(decimal(5,2), 100.0 * sp.rows_sampled / sp.rows) ELSE NULL END AS sample_pct,
                sp.modification_counter
            FROM sys.stats s
            JOIN sys.objects o ON o.object_id = s.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            CROSS APPLY sys.dm_db_stats_properties(s.object_id, s.stats_id) sp
            WHERE sp.rows > 0
            ORDER BY CASE WHEN sp.rows > 0 THEN 1.0 * sp.modification_counter / sp.rows ELSE 0 END DESC
            """;

        public const string TopQueriesByCpu = """
            SELECT TOP 20
                CONVERT(bigint, qs.total_worker_time / 1000) AS total_cpu_ms,
                CONVERT(bigint, qs.total_elapsed_time / 1000) AS total_elapsed_ms,
                qs.total_logical_reads,
                qs.execution_count,
                CONVERT(bigint, qs.total_worker_time / NULLIF(qs.execution_count, 0) / 1000) AS avg_cpu_ms,
                qs.last_execution_time,
                SUBSTRING(st.text, (qs.statement_start_offset/2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2) + 1) AS statement_text
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID() OR st.dbid IS NULL
            ORDER BY qs.total_worker_time DESC
            """;

        public const string TopQueriesByReads = """
            SELECT TOP 20
                qs.total_logical_reads,
                CONVERT(bigint, qs.total_logical_reads / NULLIF(qs.execution_count, 0)) AS avg_logical_reads,
                CONVERT(bigint, qs.total_worker_time / 1000) AS total_cpu_ms,
                qs.execution_count,
                qs.last_execution_time,
                SUBSTRING(st.text, (qs.statement_start_offset/2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2) + 1) AS statement_text
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID() OR st.dbid IS NULL
            ORDER BY qs.total_logical_reads DESC
            """;

        public const string StoredProcedureStats = """
            SELECT TOP 25
                OBJECT_SCHEMA_NAME(ps.object_id) AS schema_name,
                OBJECT_NAME(ps.object_id) AS procedure_name,
                ps.execution_count,
                CONVERT(bigint, ps.total_worker_time / 1000) AS total_cpu_ms,
                CONVERT(bigint, ps.total_elapsed_time / 1000) AS total_elapsed_ms,
                CONVERT(bigint, ps.total_elapsed_time / NULLIF(ps.execution_count, 0) / 1000) AS avg_elapsed_ms,
                ps.total_logical_reads,
                ps.last_execution_time
            FROM sys.dm_exec_procedure_stats ps
            WHERE ps.database_id = DB_ID()
            ORDER BY ps.total_worker_time DESC
            """;

        public const string WaitStatistics = """
            SELECT TOP 20
                wait_type,
                waiting_tasks_count,
                CONVERT(bigint, wait_time_ms) AS wait_time_ms,
                CONVERT(bigint, signal_wait_time_ms) AS signal_wait_time_ms,
                CONVERT(decimal(5,2), 100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0)) AS pct_of_total
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT IN (
                'CLR_SEMAPHORE','LAZYWRITER_SLEEP','RESOURCE_QUEUE','SQLTRACE_BUFFER_FLUSH','SLEEP_TASK',
                'SLEEP_SYSTEMTASK','WAITFOR','HADR_FILESTREAM_IOMGR_IOCOMPLETION','CHECKPOINT_QUEUE',
                'REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT','XE_DISPATCHER_WAIT','FT_IFTS_SCHEDULER_IDLE_WAIT',
                'LOGMGR_QUEUE','BROKER_TASK_STOP','BROKER_TO_FLUSH','BROKER_EVENTHANDLER','BROKER_TRANSMITTER',
                'BROKER_RECEIVE_WAITFOR','ONDEMAND_TASK_QUEUE','DBMIRROR_EVENTS_QUEUE','DBMIRRORING_CMD',
                'DISPATCHER_QUEUE_SEMAPHORE','SP_SERVER_DIAGNOSTICS_SLEEP','HADR_CLUSAPI_CALL','HADR_LOGCAPTURE_WAIT',
                'HADR_NOTIFICATION_DEQUEUE','HADR_TIMER_TASK','HADR_WORK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
                'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_SHUTDOWN_QUEUE',
                'DIRTY_PAGE_POLL','SOS_WORK_DISPATCHER','SLEEP_DBSTARTUP','SLEEP_DCOMSTARTUP','SLEEP_MASTERDBREADY',
                'SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','SLEEP_TEMPDBSTARTUP',
                'SNI_HTTP_ACCEPT','WAIT_XTP_HOST_WAIT','WAIT_XTP_OFFLINE_CKPT_NEW_LOG','WAIT_XTP_CKPT_CLOSE',
                'XE_LIVE_TARGET_TVF','VDI_CLIENT_OTHER','PVS_PREALLOCATE','PWAIT_ALL_COMPONENTS_INITIALIZED',
                'PWAIT_DIRECTLOGCONSUMER_GETNEXT','SQLTRACE_INCREMENTAL_FLUSH_SLEEP')
              AND waiting_tasks_count > 0
            ORDER BY wait_time_ms DESC
            """;

        public const string TableSizes = """
            SELECT TOP 25
                OBJECT_SCHEMA_NAME(ps.object_id) AS schema_name,
                OBJECT_NAME(ps.object_id) AS table_name,
                SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END) AS row_count,
                CONVERT(bigint, SUM(ps.reserved_page_count) * 8 / 1024) AS reserved_mb,
                CONVERT(bigint, SUM(ps.used_page_count) * 8 / 1024) AS used_mb,
                COUNT(DISTINCT CASE WHEN ps.index_id > 1 THEN ps.index_id END) AS nonclustered_index_count
            FROM sys.dm_db_partition_stats ps
            JOIN sys.objects o ON o.object_id = ps.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            GROUP BY ps.object_id
            ORDER BY SUM(ps.reserved_page_count) DESC
            """;

        public const string DatabaseConfiguration = """
            SELECT
                d.name AS database_name,
                d.compatibility_level,
                d.recovery_model_desc,
                d.is_read_committed_snapshot_on,
                d.snapshot_isolation_state_desc,
                d.is_auto_create_stats_on,
                d.is_auto_update_stats_on,
                d.is_auto_update_stats_async_on,
                d.is_parameterization_forced,
                d.is_query_store_on,
                d.page_verify_option_desc,
                d.is_auto_shrink_on,
                d.is_auto_close_on
            FROM sys.databases d
            WHERE d.database_id = DB_ID()
            """;

        public const string ScopedConfiguration = """
            SELECT name, CONVERT(nvarchar(256), value) AS value, is_value_default
            FROM sys.database_scoped_configurations
            ORDER BY name
            """;

        public const string ProceduresAndViews = """
            SELECT
                SCHEMA_NAME(o.schema_id) AS schema_name,
                o.name AS object_name,
                o.type_desc,
                o.create_date,
                o.modify_date,
                LEN(ISNULL(OBJECT_DEFINITION(o.object_id), '')) AS definition_length
            FROM sys.objects o
            WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF')
              AND o.is_ms_shipped = 0
            ORDER BY o.type_desc, SCHEMA_NAME(o.schema_id), o.name
            """;
    }

    [Description("Lists missing indexes the SQL Server optimizer has recorded, ranked by estimated improvement measure (higher = more impactful). Shows equality/inequality/included columns for each suggestion.")]
    public string GetMissingIndexes() =>
        RunQuery("GetMissingIndexes", Queries.MissingIndexes);

    [Description("Lists fragmented indexes (>=5% fragmentation, >=100 pages) with fragmentation percentage and page counts. Rebuild is typically advised above 30%, reorganize between 5-30%.")]
    public string GetIndexFragmentation() =>
        RunQuery("GetIndexFragmentation", Queries.IndexFragmentation);

    [Description("Lists index usage statistics (seeks/scans/lookups/updates) for all user-table indexes, least-read first. Indexes with zero reads but many updates are candidates for removal; heavy scans may indicate missing better indexes.")]
    public string GetIndexUsageStats() =>
        RunQuery("GetIndexUsageStats", Queries.IndexUsage);

    [Description("Lists statistics health for user tables: last update time, rows sampled percentage, and modification counter since last update. High modification ratios or old last_updated indicate stale statistics.")]
    public string GetStatisticsHealth() =>
        RunQuery("GetStatisticsHealth", Queries.StatisticsHealth);

    [Description("Lists the top 20 cached query statements by total CPU or logical reads, with execution counts and the statement text. sortBy must be 'cpu' or 'reads'.")]
    public string GetTopQueries(
        [Description("Sort order: 'cpu' (total worker time) or 'reads' (total logical reads)")] string sortBy = "cpu")
    {
        var sql = string.Equals(sortBy, "reads", StringComparison.OrdinalIgnoreCase)
            ? Queries.TopQueriesByReads
            : Queries.TopQueriesByCpu;
        return RunQuery("GetTopQueries", sql);
    }

    [Description("Lists the top 25 stored procedures by total CPU time with execution counts, average elapsed time, and logical reads.")]
    public string GetStoredProcedureStats() =>
        RunQuery("GetStoredProcedureStats", Queries.StoredProcedureStats);

    [Description("Lists the top server wait types (benign system waits filtered out) with wait counts, total wait time, and percentage of total. Reveals whether the server is I/O-, CPU-, memory-, or lock-bound.")]
    public string GetWaitStatistics() =>
        RunQuery("GetWaitStatistics", Queries.WaitStatistics);

    [Description("Lists the 25 largest user tables by reserved size with row counts and nonclustered index counts.")]
    public string GetTableSizes() =>
        RunQuery("GetTableSizes", Queries.TableSizes);

    [Description("Shows database-level configuration relevant to performance: compatibility level, RCSI, auto statistics settings, query store, auto shrink/close, plus database-scoped configurations (MAXDOP, legacy CE, etc.).")]
    public string GetDatabaseConfiguration()
    {
        var main = RunQuery("GetDatabaseConfiguration", Queries.DatabaseConfiguration);
        var scoped = RunQuery("GetDatabaseConfiguration(scoped)", Queries.ScopedConfiguration);
        return $"## Database options\n{main}\n\n## Database-scoped configurations\n{scoped}";
    }

    [Description("Lists all user stored procedures, views, and functions with creation/modification dates. Use GetObjectDefinition to read their source code.")]
    public string ListProceduresAndViews() =>
        RunQuery("ListProceduresAndViews", Queries.ProceduresAndViews);

    private string RunQuery(string toolName, string sql)
    {
        _logger.LogDebug("AI tool: {Tool} executing", toolName);
        try
        {
            var rows = _executor.ExecuteQuery(_connection, sql);
            if (rows.Count == 0)
                return "(no results)";

            var sb = new StringBuilder();
            var columns = rows[0].Keys.ToList();
            sb.AppendLine(string.Join("\t", columns));
            sb.AppendLine(new string('-', columns.Count * 16));

            foreach (var row in rows)
            {
                if (sb.Length >= _maxResponseBytes)
                {
                    sb.AppendLine($"\n... truncated at {_maxResponseBytes} bytes ({rows.Count} total rows) ...");
                    break;
                }
                sb.AppendLine(string.Join("\t", columns.Select(c => row.GetValueOrDefault(c, "NULL"))));
            }

            return Truncate(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI tool: {Tool} failed", toolName);
            return $"ERROR: {ex.Message}";
        }
    }

    private string Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _maxResponseBytes)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var truncated = Encoding.UTF8.GetString(bytes, 0, _maxResponseBytes);
        return truncated + $"\n... truncated at {_maxResponseBytes} bytes ...";
    }

    public void Dispose() { }
}
