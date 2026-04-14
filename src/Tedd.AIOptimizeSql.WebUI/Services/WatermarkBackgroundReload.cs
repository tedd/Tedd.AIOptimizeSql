namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// Polls a DB watermark on an interval and reloads the page when it changes.
/// </summary>
/// <remarks>
/// Blazor Server uses a per-circuit synchronization context. Background work (e.g. <see cref="PeriodicTimer"/>)
/// runs off that context; use <c>dispatchToUi</c> (typically <c>InvokeAsync</c>) for state updates, then
/// <see href="https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context">schedule a render with <c>await InvokeAsync(StateHasChanged)</c></see>
/// via <paramref name="requestRenderAsync"/>.
/// </remarks>
public static class WatermarkBackgroundReload
{
    /// <param name="requestRenderAsync">
    /// From the owning component, pass a delegate that returns <c>InvokeAsync(StateHasChanged)</c> so the render is queued on the circuit dispatcher.
    /// </param>
    /// <param name="dispatchToUi">
    /// Marshals work to the component sync context, e.g. <c>work => InvokeAsync(work)</c> from the owning component.
    /// </param>
    public static void Start(
        TimeSpan interval,
        CancellationToken pageCancellation,
        Func<Task<DateTime?>> fetchWatermarkAsync,
        Func<Func<Task>, Task> dispatchToUi,
        Func<Task> reloadAsync,
        Func<Task> requestRenderAsync)
    {
        _ = RunAsync(interval, pageCancellation, fetchWatermarkAsync, dispatchToUi, reloadAsync, requestRenderAsync);
    }

    private static async Task RunAsync(
        TimeSpan interval,
        CancellationToken pageCancellation,
        Func<Task<DateTime?>> fetchWatermarkAsync,
        Func<Func<Task>, Task> dispatchToUi,
        Func<Task> reloadAsync,
        Func<Task> requestRenderAsync)
    {
        DateTime? last = null;
        try
        {
            try
            {
                last = await fetchWatermarkAsync();
            }
            catch
            {
                // ignore initial failure
            }

            using var timer = new PeriodicTimer(interval);
            while (!pageCancellation.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(pageCancellation);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                DateTime? w;
                try
                {
                    w = await fetchWatermarkAsync();
                }
                catch
                {
                    continue;
                }

                if (w is null)
                    continue;

                if (!last.HasValue || last.Value != w.Value)
                {
                    last = w;
                    await dispatchToUi(reloadAsync);
                    await requestRenderAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
