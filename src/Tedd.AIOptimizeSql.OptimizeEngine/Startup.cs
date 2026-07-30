using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Tedd.AIOptimizeSql.OptimizeEngine.Models;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;
using Tedd.AIOptimizeSql.OptimizeEngine.Services.SqlBrowser;

namespace Tedd.AIOptimizeSql.OptimizeEngine;

public static class Startup
{
    public const string OptimizeEngineConfigurationSectionName = "OptimizeEngine";

    /// <summary>
    /// Interactive services the web UI needs regardless of whether the background engine
    /// runs in this process: catalog browsing, ad-hoc query execution, dependency graphs,
    /// and the Create Experiment wizard.
    /// </summary>
    public static void ConfigureSqlBrowserServices(IHostApplicationBuilder builder)
    {
        builder.Services.Configure<OptimizeEngineSettings>(
            builder.Configuration.GetSection(OptimizeEngineConfigurationSectionName));

        builder.Services.TryAddSingleton<ISqlCatalogService, SqlCatalogService>();
        builder.Services.TryAddSingleton<IAdHocQueryService, AdHocQueryService>();
        builder.Services.TryAddSingleton<IObjectDependencyService, ObjectDependencyService>();
        builder.Services.TryAddSingleton<IExperimentBlueprintService, ExperimentBlueprintService>();

        // Also registered by ConfigureServices when the engine runs in-process; TryAdd keeps
        // a single instance either way.
        builder.Services.TryAddSingleton<AiAgentFactory>();
        builder.Services.TryAddSingleton<ISchemaDiscoveryService, SchemaDiscoveryService>();
    }

    public static void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.Configure<OptimizeEngineSettings>(
            builder.Configuration.GetSection(OptimizeEngineConfigurationSectionName));

        builder.Services.TryAddSingleton<AiAgentFactory>();
        builder.Services.TryAddSingleton<ISchemaDiscoveryService, SchemaDiscoveryService>();
        builder.Services.AddSingleton<ResearchIterationLogger>();
        builder.Services.AddSingleton<HypothesisTestingService>();
        builder.Services.AddSingleton<IAiHypothesisService, AiHypothesisService>();
        builder.Services.AddSingleton<ResearchIterationProcessingEngine>();
        builder.Services.AddHostedService<QueueMonitorService>();

        builder.Services.AddSingleton<AgentTaskLoopRunner>();
        builder.Services.AddSingleton<PerformanceSnapshotService>();
        builder.Services.AddSingleton<DatabaseAnalysisService>();
        builder.Services.AddHostedService<AnalysisMonitorService>();
    }

    public static void ConfigureApplication(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
    }
}
