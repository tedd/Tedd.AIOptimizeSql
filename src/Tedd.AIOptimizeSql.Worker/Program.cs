using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        var aiOptimizeCs = NormalizeSqlServerConnectionForEf(
            builder.Configuration.GetConnectionString("AIOptimizeDb"));

        builder.Services.AddDbContextFactory<AIOptimizeDbContext>(options =>
            options.UseSqlServer(aiOptimizeCs));

        builder.Services.Configure<OptimizeEngineSettings>(
            builder.Configuration.GetSection("OptimizeEngine"));

        builder.Services.AddScoped<IAIOptimizeDataAccess, AIOptimizeDataAccess>();
        builder.Services.AddSingleton<AiAgentFactory>();
        builder.Services.AddSingleton<IAiHypothesisService, AiHypothesisService>();
        builder.Services.AddSingleton<BatchProcessingEngine>();
        builder.Services.AddHostedService<QueueMonitorService>();

        var host = builder.Build();
        host.Run();
    }

    private static string? NormalizeSqlServerConnectionForEf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var csBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            MultipleActiveResultSets = true
        };
        return csBuilder.ConnectionString;
    }
}
