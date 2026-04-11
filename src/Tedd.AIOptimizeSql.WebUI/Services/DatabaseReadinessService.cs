using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using Tedd.AIOptimizeSql.Database;

namespace Tedd.AIOptimizeSql.WebUI.Services;

public sealed class DatabaseReadinessService : IDatabaseReadinessService
{
    private const string ConnectionName = "AIOptimizeDb";

    private readonly IDbContextFactory<AIOptimizeDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public DatabaseReadinessService(
        IDbContextFactory<AIOptimizeDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<DatabaseReadinessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var cs = _configuration.GetConnectionString(ConnectionName);
        var info = TryParseConnectionString(cs);

        if (string.IsNullOrWhiteSpace(cs))
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.MissingConfiguration,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: $"Connection string '{ConnectionName}' is not configured.",
                TechnicalDetails: null,
                ConnectionInfo: info);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                return await BuildConnectivityFailureStatusAsync(cs, info, technicalDetails: null, cancellationToken);
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? new DatabaseReadinessStatus(
                    State: DatabaseReadinessState.Ready,
                    DatabaseExists: true,
                    PendingMigrations: [],
                    Message: "Database is up to date.",
                    TechnicalDetails: null,
                    ConnectionInfo: info)
                : new DatabaseReadinessStatus(
                    State: DatabaseReadinessState.PendingMigrations,
                    DatabaseExists: true,
                    PendingMigrations: pending,
                    Message: "The database is reachable, but migrations still need to be applied.",
                    TechnicalDetails: null,
                    ConnectionInfo: info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsLikelyConnectivityIssue(ex))
        {
            return await BuildConnectivityFailureStatusAsync(cs, info, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.Error,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: "Checking database status failed unexpectedly.",
                TechnicalDetails: ex.Message,
                ConnectionInfo: info);
        }
    }

    public async Task<DatabaseReadinessStatus> ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var cs = _configuration.GetConnectionString(ConnectionName);
        var info = TryParseConnectionString(cs);

        if (string.IsNullOrWhiteSpace(cs))
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.MissingConfiguration,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: $"Connection string '{ConnectionName}' is not configured.",
                TechnicalDetails: null,
                ConnectionInfo: info);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
            return await GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsLikelyConnectivityIssue(ex))
        {
            return await BuildMigrationFailureStatusAsync(cs, info, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.Error,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: "Applying migrations failed.",
                TechnicalDetails: ex.Message,
                ConnectionInfo: info);
        }
    }

    private static async Task<DatabaseReadinessStatus> BuildConnectivityFailureStatusAsync(
        string connectionString,
        SqlConnectionDisplayInfo? info,
        string? technicalDetails,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var targetDatabase = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? null : builder.InitialCatalog;

            builder.InitialCatalog = "master";
            if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
                builder.AttachDBFilename = string.Empty;

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            bool? databaseExists = null;
            if (!string.IsNullOrWhiteSpace(targetDatabase))
                databaseExists = await DatabaseExistsAsync(connection, targetDatabase, cancellationToken);

            var message = databaseExists == false
                ? "SQL Server is reachable, but the configured database does not exist yet or is not visible to the current login. If this login can create databases, use the button below to create it and apply migrations."
                : "SQL Server is reachable, but the configured database could not be opened. Check database permissions or database state, or try applying migrations if appropriate.";

            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.DatabaseUnavailable,
                DatabaseExists: databaseExists,
                PendingMigrations: [],
                Message: message,
                TechnicalDetails: technicalDetails,
                ConnectionInfo: info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.Error,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: "The configured connection string is invalid.",
                TechnicalDetails: ex.Message,
                ConnectionInfo: info);
        }
        catch (Exception ex) when (IsLikelyConnectivityIssue(ex))
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.ServerUnavailable,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: "Could not reach SQL Server with the current connection settings. Check server name, network access, and credentials, then refresh.",
                TechnicalDetails: technicalDetails ?? ex.Message,
                ConnectionInfo: info);
        }
        catch (Exception ex)
        {
            return new DatabaseReadinessStatus(
                State: DatabaseReadinessState.Error,
                DatabaseExists: null,
                PendingMigrations: [],
                Message: "Checking database status failed unexpectedly.",
                TechnicalDetails: technicalDetails ?? ex.Message,
                ConnectionInfo: info);
        }
    }

    private static async Task<DatabaseReadinessStatus> BuildMigrationFailureStatusAsync(
        string connectionString,
        SqlConnectionDisplayInfo? info,
        string technicalDetails,
        CancellationToken cancellationToken)
    {
        var status = await BuildConnectivityFailureStatusAsync(connectionString, info, technicalDetails, cancellationToken);

        return status.State switch
        {
            DatabaseReadinessState.DatabaseUnavailable when status.DatabaseExists == false => status with
            {
                Message = "SQL Server is reachable, but the database could not be created automatically. Ensure the configured login can create databases, then try again."
            },
            DatabaseReadinessState.DatabaseUnavailable => status with
            {
                Message = "SQL Server is reachable, but applying migrations failed. Check database permissions or database state, then try again."
            },
            DatabaseReadinessState.ServerUnavailable => status with
            {
                Message = "Applying migrations failed because SQL Server could not be reached. Check connectivity and try again."
            },
            _ => status with
            {
                Message = "Applying migrations failed."
            }
        };
    }

    private static async Task<bool> DatabaseExistsAsync(
        SqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@databaseName)";
        command.Parameters.AddWithValue("@databaseName", databaseName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    private static bool IsLikelyConnectivityIssue(Exception ex)
    {
        return ex is SqlException or TimeoutException
            || (ex.InnerException is not null && IsLikelyConnectivityIssue(ex.InnerException));
    }

    private static SqlConnectionDisplayInfo? TryParseConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            string userDisplay;
            if (builder.IntegratedSecurity)
                userDisplay = "Windows integrated";
            else if (!string.IsNullOrEmpty(builder.UserID))
                userDisplay = builder.UserID;
            else
                userDisplay = "SQL authentication (user not specified in connection string)";

            var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "(not set)" : builder.InitialCatalog;
            var server = string.IsNullOrEmpty(builder.DataSource) ? "(not set)" : builder.DataSource;

            return new SqlConnectionDisplayInfo(server, database, userDisplay);
        }
        catch
        {
            return null;
        }
    }
}
