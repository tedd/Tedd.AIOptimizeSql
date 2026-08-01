using Tedd.AIOptimizeSql.Database.Models;

namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// The database the current page is working inside, cascaded from the layout so nav links,
/// breadcrumbs and dialogs do not each have to re-read the connection from their route.
/// </summary>
/// <param name="Id">Route id, as it appears in <c>/db/{id}/…</c>.</param>
/// <param name="Name">Display name of the connection.</param>
/// <param name="AnalyzeOnly">Production-safe connection: nothing may modify the target database.</param>
/// <param name="AiConnectionId">
/// The AI bound to this database, or null when the binding was cleared. A null here is what
/// makes the UI ask for an AI before it will run anything.
/// </param>
/// <param name="AiConnectionName">Display name of the bound AI, when there is one.</param>
public sealed record DatabaseScope(
    int Id,
    string Name,
    bool AnalyzeOnly,
    int? AiConnectionId,
    string? AiConnectionName)
{
    public DatabaseConnectionId TypedId => (DatabaseConnectionId)Id;

    public AIConnectionId? TypedAiConnectionId =>
        AiConnectionId is { } id ? (AIConnectionId)id : null;

    public bool HasAi => AiConnectionId is not null;
}
