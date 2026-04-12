using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.Tests;

public class BenchmarkConfigTests
{
    [Fact]
    public void Defaults_are_expected()
    {
        var config = new BenchmarkConfig();

        Assert.Equal("MSSQL", config.DatabaseType);
        Assert.Equal(250, config.PostClearStabilizationMs);
    }
}
