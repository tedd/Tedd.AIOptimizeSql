using MudBlazor;

namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// Helpers so navigation away or MudTable reload cancellation does not surface
/// <see cref="OperationCanceledException"/> as a Blazor renderer error.
/// </summary>
public static class PageLoadCancellation
{
    /// <summary>
    /// Links MudBlazor's server-load token with the page lifetime, passes the result to EF queries,
    /// and returns an empty table when the operation is canceled.
    /// </summary>
    public static async Task<TableData<T>> RunTableLoadAsync<T>(
        Func<CancellationToken, Task<TableData<T>>> load,
        CancellationToken pageLifetime,
        CancellationToken mudTableCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pageLifetime, mudTableCancellation);
        var token = linked.Token;
        try
        {
            return await load(token);
        }
        catch (OperationCanceledException)
        {
            return new TableData<T> { TotalItems = 0, Items = Array.Empty<T>() };
        }
    }

    /// <summary>
    /// Runs page initialization; swallows cancellation when the user navigates away or a new load supersedes this one.
    /// </summary>
    public static async Task RunPageLoadAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
