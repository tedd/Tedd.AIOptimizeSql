using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.OptimizeEngine;

public static class Startup
{
    public const string OptimizeEngineConfigurationSectionName = "OptimizeEngine";

    public static void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.Configure<OptimizeEngineSettings>(
            builder.Configuration.GetSection(OptimizeEngineConfigurationSectionName));

        builder.Services.AddSingleton<AiAgentFactory>();
        builder.Services.AddSingleton<IAiHypothesisService, AiHypothesisService>();
        builder.Services.AddSingleton<BatchProcessingEngine>();
        builder.Services.AddHostedService<QueueMonitorService>();
    }

    public static void ConfigureApplication(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
    }
}
