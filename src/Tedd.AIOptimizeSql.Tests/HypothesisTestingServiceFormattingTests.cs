using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class HypothesisTestingServiceFormattingTests
{
    [Fact]
    public void WithSql_appends_sql_block()
    {
        var result = HypothesisTestingService.WithSql("Applying", "SELECT 1");

        Assert.Equal("Applying\n[sql]\nSELECT 1\n[/sql]", result);
    }

    [Fact]
    public void WithSql_without_sql_returns_message_unchanged()
    {
        Assert.Equal("Applying", HypothesisTestingService.WithSql("Applying", null));
        Assert.Equal("Applying", HypothesisTestingService.WithSql("Applying", "  "));
    }

    [Fact]
    public void WithOutput_appends_output_block()
    {
        var result = HypothesisTestingService.WithOutput("Run 1/5 done", "Table 'X'. Scan count 1, logical reads 42");

        Assert.Equal("Run 1/5 done\n[output]\nTable 'X'. Scan count 1, logical reads 42\n[/output]", result);
    }

    [Fact]
    public void WithOutput_without_output_returns_message_unchanged()
    {
        Assert.Equal("Run 1/5 done", HypothesisTestingService.WithOutput("Run 1/5 done", null));
        Assert.Equal("Run 1/5 done", HypothesisTestingService.WithOutput("Run 1/5 done", " \n "));
    }

    [Fact]
    public void WithOutput_truncates_long_output()
    {
        var longOutput = new string('x', 10_000);

        var result = HypothesisTestingService.WithOutput("Run", longOutput);

        Assert.Contains("… (truncated)", result);
        Assert.True(result.Length < longOutput.Length);
    }

    [Fact]
    public void DescribeRun_sums_parse_and_execution_times()
    {
        var timing = new SqlExecutionResult
        {
            ParseAndCompileCpuTimeMs = 10,
            ParseAndCompileElapsedTimeMs = 20,
            ExecutionCpuTimeMs = 300,
            ExecutionElapsedTimeMs = 1_000,
            TotalLogicalReads = 60_297,
            TotalPhysicalReads = 12,
        };

        var result = HypothesisTestingService.DescribeRun("Timed run 2/5", TimeSpan.FromSeconds(3.2), timing);

        Assert.StartsWith("Timed run 2/5 completed in 3.2 s", result);
        Assert.Contains("server elapsed 1,020 ms", result);
        Assert.Contains("CPU 310 ms", result);
        Assert.Contains("logical reads 60,297", result);
        Assert.Contains("physical reads 12", result);
    }

    [Theory]
    [InlineData(2.5, "2.5 s")]
    [InlineData(42, "42 s")]
    [InlineData(65, "1 m 05 s")]
    [InlineData(3_723, "1 h 02 m 03 s")]
    public void FormatDuration_uses_explicit_units(double seconds, string expected)
    {
        Assert.Equal(expected, HypothesisTestingService.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
