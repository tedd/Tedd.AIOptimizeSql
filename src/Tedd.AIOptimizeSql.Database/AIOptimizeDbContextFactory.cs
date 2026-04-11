using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Tedd.AIOptimizeSql.Database;

/// <summary>
/// Design-time factory for EF Core CLI. Example:
/// <c>dotnet ef migrations add InitialCreate --project Tedd.AIOptimizeSql.Database --startup-project Tedd.AIOptimizeSql.WebUI --context AIOptimizeDbContext</c>
/// (run from the <c>src</c> directory, or adjust paths).
/// </summary>
public sealed class AIOptimizeDbContextFactory : IDesignTimeDbContextFactory<AIOptimizeDbContext>
{
    public AIOptimizeDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<AIOptimizeDbContext>();
        optionsBuilder.UseSqlServer(connectionString);
        return new AIOptimizeDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString()
    {
        foreach (var basePath in GetAppsettingsSearchPaths())
        {
            var jsonPath = Path.Combine(basePath, "appsettings.json");
            if (!File.Exists(jsonPath))
                continue;

            var config = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var cs = config.GetConnectionString("AIOptimizeDb");
            if (!string.IsNullOrWhiteSpace(cs))
                return cs;
        }

        return "Server=(localdb)\\MSSQLLocalDB;Database=AIOptimizeSql;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
    }

    private static IEnumerable<string> GetAppsettingsSearchPaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return cwd;
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "Tedd.AIOptimizeSql.WebUI"));
        yield return Path.GetFullPath(Path.Combine(cwd, "Tedd.AIOptimizeSql.WebUI"));
    }
}
