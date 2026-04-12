using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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

        var aiOptimizeCs = NormalizeSqlServerConnectionForEf(
            builder.Configuration.GetConnectionString("AIOptimizeDb"));

        builder.Services.AddDbContextFactory<AIOptimizeDbContext>(options =>
            options.UseSqlServer(aiOptimizeCs));

        builder.Services.AddScoped<IAIOptimizeDataAccess, AIOptimizeDataAccess>();

        Startup.ConfigureServices(builder);

        var host = builder.Build();
        Startup.ConfigureApplication(host);
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
