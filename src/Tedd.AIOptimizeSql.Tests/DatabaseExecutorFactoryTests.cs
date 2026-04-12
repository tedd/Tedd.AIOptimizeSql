using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class DatabaseExecutorFactoryTests
{
    [Theory]
    [InlineData("MSSQL")]
    [InlineData("mssql")]
    [InlineData(" MsSql ")]
    public void Create_returns_MsSqlExecutor_for_supported_type(string databaseType)
    {
        var config = new BenchmarkConfig { DatabaseType = databaseType };

        var executor = DatabaseExecutorFactory.Create(config, _ => { });

        Assert.IsType<MsSqlExecutor>(executor);
    }

    [Fact]
    public void Create_throws_for_unsupported_database_type()
    {
        var config = new BenchmarkConfig { DatabaseType = "PostgreSQL" };

        var ex = Assert.Throws<NotSupportedException>(() => DatabaseExecutorFactory.Create(config, _ => { }));

        Assert.Contains("POSTGRESQL", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MSSQL", ex.Message, StringComparison.Ordinal);
    }
}
