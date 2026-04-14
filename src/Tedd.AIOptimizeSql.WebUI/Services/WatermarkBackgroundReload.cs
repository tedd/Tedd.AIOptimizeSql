namespace Tedd.AIOptimizeSql.WebUI.Services;

/// <summary>
/// Polls a DB watermark on an interval and reloads the page when it changes.
/// </summary>
public static class WatermarkBackgroundReload
{
    /// <param name="dispatchToUi">
    /// Marshals work to the component sync context, e.g. <c>work => InvokeAsync(work)</c> from the owning component.
    /// </param>
    public static void Start(
        TimeSpan interval,
        CancellationToken pageCancellation,
        Func<Task<DateTime?>> fetchWatermarkAsync,
        Func<Func<Task>, Task> dispatchToUi,
        Func<Task> reloadAsync)
    {
        _ = RunAsync(interval, pageCancellation, fetchWatermarkAsync, dispatchToUi, reloadAsync);
    }

    private static async Task RunAsync(
        TimeSpan interval,
        CancellationToken pageCancellation,
        Func<Task<DateTime?>> fetchWatermarkAsync,
        Func<Func<Task>, Task> dispatchToUi,
        Func<Task> reloadAsync)
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
                    await dispatchToUi(async () =>
                    {
                        await reloadAsync();
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
