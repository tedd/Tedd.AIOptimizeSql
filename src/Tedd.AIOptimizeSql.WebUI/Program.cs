using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using MudBlazor.Services;

using Tedd.AIOptimizeSql.Database;
using Tedd.AIOptimizeSql.Database.DataAccess;
using Tedd.AIOptimizeSql.WebUI.Components;
using Tedd.AIOptimizeSql.WebUI.Options;
using Tedd.AIOptimizeSql.WebUI.Security;
using Tedd.AIOptimizeSql.WebUI.Services;

namespace Tedd.AIOptimizeSql.WebUI;

public class Program
{
    public static void Main(string[] args)
    {
        // The standalone single-file executable self-extracts its content (wwwroot,
        // static asset manifests, assemblies) to a temp directory, which becomes
        // AppContext.BaseDirectory while the exe itself lives elsewhere. Detect that
        // split and anchor the content root at the extraction directory so static
        // assets resolve; in every other layout the two directories are the same.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        var isSelfExtractedBundle = exeDir is not null && !string.Equals(
            Path.TrimEndingDirectorySeparator(exeDir),
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = isSelfExtractedBundle ? AppContext.BaseDirectory : null
        });

        // The content root of the extracted bundle is a temp directory, so an
        // appsettings.json the user drops NEXT TO THE EXE would be ignored. Load it
        // explicitly, then re-add env vars and command line so their precedence stays
        // above JSON files.
        if (isSelfExtractedBundle && exeDir is not null)
        {
            builder.Configuration.AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: true, reloadOnChange: true);
            builder.Configuration.AddJsonFile(Path.Combine(exeDir, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);
            builder.Configuration.AddEnvironmentVariables();
            if (args.Length > 0)
                builder.Configuration.AddCommandLine(args);
        }

        // Loopback-only default binding, optional single-user authentication (auto-required
        // on Azure App Service), optional remote-IP allowlist. See docs/DEPLOYMENT.md.
        var security = builder.AddAIOptimizeSecurity();

        builder.AddServiceDefaults();
        builder.AddAIOptimizeDatabase();

        builder.Services.AddMudServices();

        builder.Services.AddScoped<IDatabaseReadinessService, DatabaseReadinessService>();
        builder.Services.AddScoped<IAIOptimizeDataAccess, AIOptimizeDataAccess>();
        builder.Services.Configure<UiPollingOptions>(builder.Configuration.GetSection(UiPollingOptions.SectionName));

        // Combined mode: host the optimize engine inside the web process so a single
        // executable (or a single App Service) runs the whole application. Set
        // OptimizeEngine:RunInProcess=false to deploy the Worker separately instead.
        var runEngineInProcess = builder.Configuration.GetValue("OptimizeEngine:RunInProcess", true);
        if (runEngineInProcess)
            Tedd.AIOptimizeSql.OptimizeEngine.Startup.ConfigureServices(builder);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        // First in the pipeline: forwarded headers (real client address/scheme behind the
        // Azure App Service proxy) and the remote-IP allowlist.
        app.UseAIOptimizeSecurity(security);

        app.MapDefaultEndpoints();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        if (security.AuthenticationEnabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseAntiforgery();

        app.MapStaticAssets();
        var componentEndpoints = app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        if (security.AuthenticationEnabled)
        {
            // Every page (and the Blazor circuit hub) requires the signed-in user; the login
            // page and error/not-found pages opt out via [AllowAnonymous].
            componentEndpoints.RequireAuthorization();
            app.MapAuthEndpoints();
        }

        ApplySqliteMigrations(app);

        app.Start();
        TryLaunchBrowser(app);
        app.WaitForShutdown();
    }

    /// <summary>
    /// SQLite is the zero-configuration standalone store: the database is a local file
    /// owned by this app, so migrations are applied automatically at startup instead of
    /// going through the manual readiness flow used for shared SQL Server instances.
    /// </summary>
    private static void ApplySqliteMigrations(WebApplication app)
    {
        var dbOptions = app.Services.GetRequiredService<AIOptimizeDatabaseOptions>();
        if (dbOptions.Provider != AIOptimizeDatabaseProvider.Sqlite)
            return;

        using var scope = app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AIOptimizeDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        app.Logger.LogInformation("SQLite database ready: {ConnectionString}", dbOptions.ConnectionString);
    }

    /// <summary>
    /// Opens the default browser when running interactively (standalone mode). Skipped in
    /// Development (launchSettings already does it), on Azure App Service, in containers,
    /// and when LaunchBrowser=false is configured.
    /// </summary>
    private static void TryLaunchBrowser(WebApplication app)
    {
        if (!app.Configuration.GetValue("LaunchBrowser", true))
            return;
        if (app.Environment.IsDevelopment())
            return;
        if (SecuritySetup.IsAzureAppService)
            return;
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var address = app.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                      ?? app.Urls.FirstOrDefault();
        if (address is null)
            return;

        var url = address
            .Replace("0.0.0.0", "localhost")
            .Replace("[::]", "localhost")
            .Replace("//+", "//localhost")
            .Replace("//*", "//localhost");

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            app.Logger.LogInformation("Opened browser at {Url}", url);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Could not open a browser automatically. Navigate to {Url} manually.", url);
        }
    }
}
