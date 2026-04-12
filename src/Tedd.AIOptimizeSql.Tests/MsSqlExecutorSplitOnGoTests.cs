using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class MsSqlExecutorSplitOnGoTests
{
    [Fact]
    public void Splits_on_go_at_line_start_case_insensitive()
    {
        var sql = "SELECT 1\ngo\nSELECT 2\nGO\nSELECT 3";

        var batches = MsSqlExecutor.SplitOnGo(sql);

        Assert.Equal(["SELECT 1", "SELECT 2", "SELECT 3"], batches);
    }

    [Fact]
    public void Trims_whitespace_and_skips_empty_batches()
    {
        var sql = "  SELECT 1  \n\nGO\n\n  SELECT 2  ";

        var batches = MsSqlExecutor.SplitOnGo(sql);

        Assert.Equal(["SELECT 1", "SELECT 2"], batches);
    }

    [Fact]
    public void Single_batch_without_go_returns_one_statement()
    {
        var sql = "SELECT 1";

        var batches = MsSqlExecutor.SplitOnGo(sql);

        Assert.Single(batches);
        Assert.Equal("SELECT 1", batches[0]);
    }

    [Fact]
    public void Embedded_go_word_is_not_a_split()
    {
        var sql = "SELECT Category FROM dbo.Glossary";

        var batches = MsSqlExecutor.SplitOnGo(sql);

        Assert.Single(batches);
    }
}
