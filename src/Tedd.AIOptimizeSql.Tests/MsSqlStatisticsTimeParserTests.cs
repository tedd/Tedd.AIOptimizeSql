using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class MsSqlStatisticsTimeParserTests
{
    [Fact]
    public void AccumulateFromMessage_parses_single_line_case_insensitive()
    {
        var result = new SqlTimingResult();
        MsSqlStatisticsTimeParser.AccumulateFromMessage(
            "SQL Server Execution Times: CPU time = 12 ms,  elapsed time = 34 ms",
            result);

        Assert.Equal(12, result.CpuTimeMs);
        Assert.Equal(34, result.ElapsedTimeMs);
    }

    [Fact]
    public void AccumulateFromMessage_accumulates_multiple_matches_in_one_message()
    {
        var result = new SqlTimingResult();
        var msg = """
            SQL Server Execution Times: CPU time = 1 ms,  elapsed time = 2 ms
            SQL Server Execution Times: CPU time = 10 ms,  elapsed time = 20 ms
            """;

        MsSqlStatisticsTimeParser.AccumulateFromMessage(msg, result);

        Assert.Equal(11, result.CpuTimeMs);
        Assert.Equal(22, result.ElapsedTimeMs);
    }

    [Fact]
    public void AccumulateFromMessage_ignores_non_matching_text()
    {
        var result = new SqlTimingResult();
        MsSqlStatisticsTimeParser.AccumulateFromMessage("No timing here", result);

        Assert.Equal(0, result.CpuTimeMs);
        Assert.Equal(0, result.ElapsedTimeMs);
    }

    [Fact]
    public void AccumulateFromMessage_appends_to_existing_totals()
    {
        var result = new SqlTimingResult { CpuTimeMs = 5, ElapsedTimeMs = 7 };
        MsSqlStatisticsTimeParser.AccumulateFromMessage(
            "sql server execution times: cpu time = 3 ms,  elapsed time = 4 ms",
            result);

        Assert.Equal(8, result.CpuTimeMs);
        Assert.Equal(11, result.ElapsedTimeMs);
    }
}
