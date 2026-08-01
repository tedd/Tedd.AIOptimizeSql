namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// Lets a page tell the route guard that the selected database's own row changed — renaming it,
/// or binding a different AI to it — so the cascaded <see cref="DatabaseScope"/> is reloaded
/// instead of staying on the copy taken when the database was first opened.
/// </summary>
public sealed class DatabaseScopeNotifier
{
    public event Func<Task>? ScopeChanged;

    public Task NotifyChangedAsync() => ScopeChanged?.Invoke() ?? Task.CompletedTask;
}
