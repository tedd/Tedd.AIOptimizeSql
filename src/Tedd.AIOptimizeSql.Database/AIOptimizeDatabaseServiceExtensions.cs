using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tedd.AIOptimizeSql.Database;

/// <summary>
/// Single place where the AIOptimize metadata database is wired up, shared by the
/// WebUI and the Worker so both processes resolve provider and connection string
/// the same way from the same configuration keys.
/// </summary>
public static class AIOptimizeDatabaseServiceExtensions
{
    public const string ConnectionName = "AIOptimizeDb";
    public const string ProviderConfigKey = "Database:Provider";

    /// <summary>Assembly holding the SQLite variant of the migrations. The SQL Server
    /// migrations live in this (the context's) assembly, which is EF's default.</summary>
    public const string SqliteMigrationsAssembly = "Tedd.AIOptimizeSql.Database.Sqlite";

    public static IHostApplicationBuilder AddAIOptimizeDatabase(this IHostApplicationBuilder builder)
    {
        var dbOptions = Resolve(builder.Configuration);
        builder.Services.AddSingleton(dbOptions);

        // Blazor Interactive Server: UI uses IDbContextFactory<T> (short-lived or per-component contexts).
        // Do NOT also call AddDbContext — it registers conflicting scoped option services.
        builder.Services.AddDbContextFactory<AIOptimizeDbContext>(options =>
        {
            switch (dbOptions.Provider)
            {
                case AIOptimizeDatabaseProvider.Sqlite:
                    options.UseSqlite(dbOptions.ConnectionString,
                        sqlite => sqlite.MigrationsAssembly(SqliteMigrationsAssembly));
                    // The strongly-typed ID structs (int value converters) trigger a phantom
                    // Sqlite:Autoincrement diff between the snapshot (stores plain int) and the
                    // live model (converted type), so EF always thinks changes are pending under
                    // SQLite even when snapshot and model were generated seconds apart.
                    options.ConfigureWarnings(warnings =>
                        warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
                    break;
                default:
                    options.UseSqlServer(dbOptions.ConnectionString);
                    break;
            }

            options.AddInterceptors(ModifiedAtSaveChangesInterceptor.Instance);
        });

        return builder;
    }

    public static AIOptimizeDatabaseOptions Resolve(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        var provider = ResolveProvider(configuration, connectionString);

        return provider switch
        {
            AIOptimizeDatabaseProvider.Sqlite => new AIOptimizeDatabaseOptions(
                provider, PrepareSqliteConnectionString(connectionString)),
            _ => new AIOptimizeDatabaseOptions(
                provider, NormalizeSqlServerConnectionForEf(connectionString)),
        };
    }

    private static AIOptimizeDatabaseProvider ResolveProvider(IConfiguration configuration, string? connectionString)
    {
        var configured = configuration[ProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant() switch
            {
                "sqlite" => AIOptimizeDatabaseProvider.Sqlite,
                "sqlserver" or "mssql" => AIOptimizeDatabaseProvider.SqlServer,
                _ => throw new InvalidOperationException(
                    $"Unknown database provider '{configured}' in '{ProviderConfigKey}'. Supported values: Sqlite, SqlServer.")
            };
        }

        // No explicit provider: no connection string at all is the zero-config
        // standalone case (local SQLite file); otherwise sniff the string, keeping
        // the historical SQL Server behavior when in doubt.
        if (string.IsNullOrWhiteSpace(connectionString))
            return AIOptimizeDatabaseProvider.Sqlite;

        return LooksLikeSqliteConnectionString(connectionString)
            ? AIOptimizeDatabaseProvider.Sqlite
            : AIOptimizeDatabaseProvider.SqlServer;
    }

    private static bool LooksLikeSqliteConnectionString(string connectionString)
    {
        // Keywords that only appear in SQL Server connection strings.
        string[] sqlServerKeywords =
        [
            "server=", "initial catalog=", "database=", "trusted_connection", "integrated security",
            "user id=", "uid=", "encrypt=", "multipleactiveresultsets", "trustservercertificate",
            "attachdbfilename", "authentication="
        ];
        var lower = connectionString.ToLowerInvariant();
        if (sqlServerKeywords.Any(lower.Contains))
            return false;

        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            return dataSource == ":memory:"
                || dataSource.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || dataSource.EndsWith(".db3", StringComparison.OrdinalIgnoreCase)
                || dataSource.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                || dataSource.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string PrepareSqliteConnectionString(string? connectionString)
    {
        var csBuilder = string.IsNullOrWhiteSpace(connectionString)
            ? new SqliteConnectionStringBuilder { DataSource = DefaultSqlitePath() }
            : new SqliteConnectionStringBuilder(connectionString);

        if (string.IsNullOrWhiteSpace(csBuilder.DataSource))
            csBuilder.DataSource = DefaultSqlitePath();

        // Anchor relative paths to the executable's directory so the database neither
        // moves around with the process working directory nor lands in the throwaway
        // extraction directory of the self-extracted single-file build. Fall back to
        // AppContext.BaseDirectory when launched via the dotnet host ("dotnet app.dll").
        if (!Path.IsPathRooted(csBuilder.DataSource) && csBuilder.DataSource != ":memory:")
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            var isDotnetHost = string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet",
                StringComparison.OrdinalIgnoreCase);
            var anchor = !isDotnetHost && !string.IsNullOrEmpty(exeDir) ? exeDir : AppContext.BaseDirectory;
            csBuilder.DataSource = Path.Combine(anchor, csBuilder.DataSource);
        }

        var directory = Path.GetDirectoryName(csBuilder.DataSource);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return csBuilder.ConnectionString;
    }

    private static string DefaultSqlitePath()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "Tedd.AIOptimizeSql", "aioptimize.db");
    }

    /// <summary>
    /// EF Core's SQL Server provider expects MARS so overlapping commands on one connection do not fail.
    /// </summary>
    private static string NormalizeSqlServerConnectionForEf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString ?? string.Empty;

        var csBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            MultipleActiveResultSets = true
        };
        return csBuilder.ConnectionString;
    }
}
