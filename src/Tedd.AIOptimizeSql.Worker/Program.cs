using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.OptimizeEngine;

namespace Tedd.AIOptimizeSql.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddAIOptimizeDatabase();

        builder.Services.AddScoped<IAIOptimizeDataAccess, AIOptimizeDataAccess>();

        Startup.ConfigureServices(builder);

        var host = builder.Build();
        Startup.ConfigureApplication(host);
        host.Run();
    }
}
