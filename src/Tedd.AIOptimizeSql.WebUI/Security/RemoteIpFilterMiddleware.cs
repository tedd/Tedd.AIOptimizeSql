namespace Tedd.AIOptimizeSql.WebUI.Security;

/// <summary>
/// Rejects requests whose client address is not on the configured allowlist. Must run
/// after forwarded-headers processing so that behind a reverse proxy (Azure App Service)
/// the address checked is the real client, not the proxy.
/// </summary>
public sealed class RemoteIpFilterMiddleware(RequestDelegate next, RemoteIpAllowList allowList, ILogger<RemoteIpFilterMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null || !allowList.IsAllowed(remoteIp))
        {
            logger.LogWarning("Blocked request to {Path} from {RemoteIp}: address is not in Security:AllowedRemoteIPs.",
                context.Request.Path, remoteIp);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden: this address is not allowed to access the server.");
            return;
        }

        await next(context);
    }
}
