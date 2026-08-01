using MudBlazor;

using Tedd.AIOptimizeSql.Database.Models;
using Tedd.AIOptimizeSql.WebUI.Components.Dialogs;

namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// "Everything inside a database uses the AI bound to that database" — this is what happens
/// when there is no such AI yet and the user tries to run something anyway.
/// </summary>
public static class AiBindingPrompt
{
    /// <summary>
    /// Returns the AI bound to <paramref name="scope"/>, asking for one and saving it onto the
    /// database when the binding is missing. Returns null when the user backs out, in which case
    /// the caller should simply not start the work.
    /// </summary>
    public static async Task<AIConnectionId?> EnsureAiConnectionAsync(
        DatabaseScope? scope,
        IDialogService dialogService,
        string reason)
    {
        if (scope is null)
            return null;

        if (scope.TypedAiConnectionId is { } bound)
            return bound;

        var parameters = new DialogParameters<RequireAiConnectionDialog>
        {
            { x => x.DatabaseId, scope.Id },
            { x => x.DatabaseName, scope.Name },
            { x => x.Reason, reason },
        };

        var dialog = await dialogService.ShowAsync<RequireAiConnectionDialog>(
            "Which AI should this database use?",
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true });

        var result = await dialog.Result;
        if (result is null || result.Canceled || result.Data is not int aiId)
            return null;

        return (AIConnectionId)aiId;
    }
}
