namespace Tedd.AIOptimizeSql.WebUI.Services;

public sealed record SqlConnectionDisplayInfo(string Server, string Database, string UserDisplay);

public enum DatabaseReadinessState
{
    Ready,
    PendingMigrations,
    DatabaseUnavailable,
    ServerUnavailable,
    MissingConfiguration,
    Error
}

public sealed record DatabaseReadinessStatus(
    DatabaseReadinessState State,
    bool? DatabaseExists,
    IReadOnlyList<string> PendingMigrations,
    string? Message,
    string? TechnicalDetails,
    SqlConnectionDisplayInfo? ConnectionInfo)
{
    public bool IsReady => State == DatabaseReadinessState.Ready;
    public bool CanConnect => State is DatabaseReadinessState.Ready or DatabaseReadinessState.PendingMigrations;
}

public interface IDatabaseReadinessService
{
    Task<DatabaseReadinessStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<DatabaseReadinessStatus> ApplyMigrationsAsync(CancellationToken cancellationToken = default);
}
