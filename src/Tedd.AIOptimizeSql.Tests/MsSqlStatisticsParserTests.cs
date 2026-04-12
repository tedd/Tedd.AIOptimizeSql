using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class MsSqlStatisticsParserTests
{
    // ── Execution times ──────────────────────────────────────────────────────

    [Fact]
    public void AccumulateFromMessage_parses_execution_time_case_insensitive()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage(
            "SQL Server Execution Times: CPU time = 12 ms,  elapsed time = 34 ms",
            result);

        Assert.Equal(12, result.ExecutionCpuTimeMs);
        Assert.Equal(34, result.ExecutionElapsedTimeMs);
    }

    [Fact]
    public void AccumulateFromMessage_accumulates_multiple_execution_time_matches()
    {
        var result = new SqlExecutionResult();
        var msg = """
            SQL Server Execution Times: CPU time = 1 ms,  elapsed time = 2 ms
            SQL Server Execution Times: CPU time = 10 ms,  elapsed time = 20 ms
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(11, result.ExecutionCpuTimeMs);
        Assert.Equal(22, result.ExecutionElapsedTimeMs);
    }

    [Fact]
    public void AccumulateFromMessage_ignores_non_matching_text()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage("No timing here", result);

        Assert.Equal(0, result.ExecutionCpuTimeMs);
        Assert.Equal(0, result.ExecutionElapsedTimeMs);
        Assert.Equal(0, result.ParseAndCompileCpuTimeMs);
        Assert.Empty(result.TableIoStats);
        Assert.Empty(result.RowsAffected);
    }

    [Fact]
    public void AccumulateFromMessage_appends_execution_times_to_existing_totals()
    {
        var result = new SqlExecutionResult { ExecutionCpuTimeMs = 5, ExecutionElapsedTimeMs = 7 };
        MsSqlStatisticsParser.AccumulateFromMessage(
            "sql server execution times: cpu time = 3 ms,  elapsed time = 4 ms",
            result);

        Assert.Equal(8, result.ExecutionCpuTimeMs);
        Assert.Equal(11, result.ExecutionElapsedTimeMs);
    }

    // ── Parse and compile times ──────────────────────────────────────────────

    [Fact]
    public void Parses_single_parse_and_compile_time()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage(
            "SQL Server parse and compile time: \n   CPU time = 0 ms, elapsed time = 25 ms",
            result);

        Assert.Equal(0, result.ParseAndCompileCpuTimeMs);
        Assert.Equal(25, result.ParseAndCompileElapsedTimeMs);
    }

    [Fact]
    public void Accumulates_multiple_parse_and_compile_times()
    {
        var result = new SqlExecutionResult();
        var msg = """
            SQL Server parse and compile time: 
               CPU time = 0 ms, elapsed time = 25 ms
            SQL Server parse and compile time: 
               CPU time = 0 ms, elapsed time = 0 ms
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(0, result.ParseAndCompileCpuTimeMs);
        Assert.Equal(25, result.ParseAndCompileElapsedTimeMs);
    }

    [Fact]
    public void Parse_compile_does_not_interfere_with_execution_times()
    {
        var result = new SqlExecutionResult();
        var msg = """
            SQL Server parse and compile time: 
               CPU time = 5 ms, elapsed time = 10 ms
             SQL Server Execution Times:
               CPU time = 100 ms,  elapsed time = 200 ms
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(5, result.ParseAndCompileCpuTimeMs);
        Assert.Equal(10, result.ParseAndCompileElapsedTimeMs);
        Assert.Equal(100, result.ExecutionCpuTimeMs);
        Assert.Equal(200, result.ExecutionElapsedTimeMs);
    }

    // ── IO statistics ────────────────────────────────────────────────────────

    [Fact]
    public void Parses_single_table_io_stats()
    {
        var result = new SqlExecutionResult();
        var msg = "Table 'MappedTrialBalanceMonthly'. Scan count 1, logical reads 254, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.";

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Single(result.TableIoStats);
        var t = result.TableIoStats[0];
        Assert.Equal("MappedTrialBalanceMonthly", t.TableName);
        Assert.Equal(1, t.ScanCount);
        Assert.Equal(254, t.LogicalReads);
        Assert.Equal(0, t.PhysicalReads);

        Assert.Equal(1, result.TotalScanCount);
        Assert.Equal(254, result.TotalLogicalReads);
    }

    [Fact]
    public void Parses_multiple_table_io_stats_and_sums_totals()
    {
        var result = new SqlExecutionResult();
        var msg = """
            Table 'MappedTrialBalanceMonthly'. Scan count 1, logical reads 254, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.
            Table 'Worktable'. Scan count 0, logical reads 0, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(2, result.TableIoStats.Count);
        Assert.Equal("MappedTrialBalanceMonthly", result.TableIoStats[0].TableName);
        Assert.Equal("Worktable", result.TableIoStats[1].TableName);
        Assert.Equal(1, result.TotalScanCount);
        Assert.Equal(254, result.TotalLogicalReads);
    }

    [Fact]
    public void Parses_io_stats_with_all_nonzero_fields()
    {
        var result = new SqlExecutionResult();
        var msg = "Table 'BigTable'. Scan count 3, logical reads 100, physical reads 50, page server reads 10, read-ahead reads 25, page server read-ahead reads 5, lob logical reads 8, lob physical reads 4, lob page server reads 2, lob read-ahead reads 1, lob page server read-ahead reads 1.";

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        var t = result.TableIoStats[0];
        Assert.Equal(3, t.ScanCount);
        Assert.Equal(100, t.LogicalReads);
        Assert.Equal(50, t.PhysicalReads);
        Assert.Equal(10, t.PageServerReads);
        Assert.Equal(25, t.ReadAheadReads);
        Assert.Equal(5, t.PageServerReadAheadReads);
        Assert.Equal(8, t.LobLogicalReads);
        Assert.Equal(4, t.LobPhysicalReads);
        Assert.Equal(2, t.LobPageServerReads);
        Assert.Equal(1, t.LobReadAheadReads);
        Assert.Equal(1, t.LobPageServerReadAheadReads);

        Assert.Equal(3, result.TotalScanCount);
        Assert.Equal(100, result.TotalLogicalReads);
        Assert.Equal(50, result.TotalPhysicalReads);
        Assert.Equal(10, result.TotalPageServerReads);
        Assert.Equal(8, result.TotalLobLogicalReads);
    }

    // ── Rows affected ────────────────────────────────────────────────────────

    [Fact]
    public void Parses_rows_affected_singular()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage("(1 row affected)", result);

        Assert.Single(result.RowsAffected);
        Assert.Equal(1, result.RowsAffected[0]);
    }

    [Fact]
    public void Parses_rows_affected_plural()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage("(42 rows affected)", result);

        Assert.Single(result.RowsAffected);
        Assert.Equal(42, result.RowsAffected[0]);
    }

    [Fact]
    public void Parses_zero_rows_affected()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage("(0 rows affected)", result);

        Assert.Single(result.RowsAffected);
        Assert.Equal(0, result.RowsAffected[0]);
    }

    [Fact]
    public void Parses_multiple_rows_affected()
    {
        var result = new SqlExecutionResult();
        var msg = """
            (0 rows affected)
            (1 row affected)
            (5 rows affected)
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(3, result.RowsAffected.Count);
        Assert.Equal([0, 1, 5], result.RowsAffected);
    }

    // ── Full combined message ────────────────────────────────────────────────

    [Fact]
    public void Parses_full_statistics_output_from_real_query()
    {
        var result = new SqlExecutionResult();
        var msg = """
            SQL Server parse and compile time: 
               CPU time = 0 ms, elapsed time = 25 ms.
            SQL Server parse and compile time: 
               CPU time = 0 ms, elapsed time = 0 ms.

            (0 rows affected)
            Table 'MappedTrialBalanceMonthly'. Scan count 1, logical reads 254, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.
            Table 'Worktable'. Scan count 0, logical reads 0, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.

            (1 row affected)

             SQL Server Execution Times:
               CPU time = 0 ms,  elapsed time = 12 ms.
            """;

        MsSqlStatisticsParser.AccumulateFromMessage(msg, result);

        Assert.Equal(0, result.ParseAndCompileCpuTimeMs);
        Assert.Equal(25, result.ParseAndCompileElapsedTimeMs);

        Assert.Equal(0, result.ExecutionCpuTimeMs);
        Assert.Equal(12, result.ExecutionElapsedTimeMs);

        Assert.Equal(2, result.TableIoStats.Count);
        Assert.Equal("MappedTrialBalanceMonthly", result.TableIoStats[0].TableName);
        Assert.Equal(254, result.TableIoStats[0].LogicalReads);
        Assert.Equal("Worktable", result.TableIoStats[1].TableName);

        Assert.Equal(1, result.TotalScanCount);
        Assert.Equal(254, result.TotalLogicalReads);

        Assert.Equal(2, result.RowsAffected.Count);
        Assert.Equal(0, result.RowsAffected[0]);
        Assert.Equal(1, result.RowsAffected[1]);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Handles_empty_message()
    {
        var result = new SqlExecutionResult();
        MsSqlStatisticsParser.AccumulateFromMessage("", result);

        Assert.Equal(0, result.ExecutionCpuTimeMs);
        Assert.Equal(0, result.ExecutionElapsedTimeMs);
        Assert.Empty(result.TableIoStats);
        Assert.Empty(result.RowsAffected);
    }

    [Fact]
    public void Accumulates_across_multiple_calls()
    {
        var result = new SqlExecutionResult();

        MsSqlStatisticsParser.AccumulateFromMessage(
            " SQL Server Execution Times:\n   CPU time = 10 ms,  elapsed time = 20 ms.", result);
        MsSqlStatisticsParser.AccumulateFromMessage(
            " SQL Server Execution Times:\n   CPU time = 5 ms,  elapsed time = 8 ms.", result);
        MsSqlStatisticsParser.AccumulateFromMessage(
            "Table 'Foo'. Scan count 2, logical reads 100, physical reads 10, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.", result);
        MsSqlStatisticsParser.AccumulateFromMessage(
            "(3 rows affected)", result);

        Assert.Equal(15, result.ExecutionCpuTimeMs);
        Assert.Equal(28, result.ExecutionElapsedTimeMs);
        Assert.Single(result.TableIoStats);
        Assert.Equal(100, result.TotalLogicalReads);
        Assert.Equal(10, result.TotalPhysicalReads);
        Assert.Equal([3], result.RowsAffected);
    }
}
