using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using MudBlazor.Services;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.WebUI.Components;
using Tedd.AIOptimizeSql.WebUI.Services;

namespace Tedd.AIOptimizeSql.WebUI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        var aiOptimizeCs = NormalizeSqlServerConnectionForEf(
            builder.Configuration.GetConnectionString("AIOptimizeDb"));

        // AddDbContextFactory also registers AIOptimizeDbContext as a scoped service
        // (since EF Core 6), so pages that @inject AIOptimizeDbContext still work.
        // Do NOT also call AddDbContext — it registers conflicting scoped option
        // services that the singleton factory cannot resolve.
        builder.Services.AddDbContextFactory<AIOptimizeDbContext>(options =>
            options.UseSqlServer(aiOptimizeCs));

        builder.Services.AddMudServices();

        builder.Services.AddScoped<IDatabaseReadinessService, DatabaseReadinessService>();
        builder.Services.AddScoped<IAIOptimizeDataAccess, AIOptimizeDataAccess>();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        app.MapDefaultEndpoints();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }

    /// <summary>
    /// EF Core's SQL Server provider expects MARS so overlapping commands on one connection do not fail.
    /// </summary>
    private static string? NormalizeSqlServerConnectionForEf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            MultipleActiveResultSets = true
        };
        return builder.ConnectionString;
    }
}
