using Tedd.AIOptimizeSql.OptimizeEngine.Models;

namespace Tedd.AIOptimizeSql.Tests;

public class OptimizeEngineSettingsTests
{
    [Fact]
    public void Defaults_are_expected()
    {
        var settings = new OptimizeEngineSettings();

        Assert.Equal(10, settings.QueuePollIntervalSeconds);
        Assert.Equal(5, settings.BatchStateCheckIntervalSeconds);
        Assert.Equal(524_288, settings.MaxToolResponseBytes);
    }
}
