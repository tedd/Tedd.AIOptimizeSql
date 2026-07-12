using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

using Tedd.AIOptimizeSql.WebUI.Options;

namespace Tedd.AIOptimizeSql.WebUI.Security;

/// <summary>
/// Form-post endpoints backing the login page and the logout button. Only mapped when
/// authentication is enabled. Both bind form data, so the framework enforces the
/// antiforgery token rendered by the corresponding forms.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (
            HttpContext http,
            [FromForm] string? username,
            [FromForm] string? password,
            [FromForm] string? returnUrl,
            SecurityOptions options,
            ILogger<SecurityOptions> logger) =>
        {
            var target = SanitizeReturnUrl(returnUrl);

            if (!SecuritySetup.ValidateCredentials(options.Authentication, username, password))
            {
                logger.LogWarning("Failed login attempt for user {Username} from {RemoteIp}.",
                    username, http.Connection.RemoteIpAddress);
                // Blunt brute-force throttle; a single-account app needs nothing fancier.
                await Task.Delay(TimeSpan.FromSeconds(1), http.RequestAborted);
                return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(target)}");
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, options.Authentication.Username)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            logger.LogInformation("User {Username} signed in from {RemoteIp}.",
                options.Authentication.Username, http.Connection.RemoteIpAddress);
            return Results.Redirect(target);
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext http, IFormCollection _) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    /// <summary>Only same-site absolute paths survive; anything else falls back to "/".</summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return "/";
        if (returnUrl[0] != '/' || returnUrl.StartsWith("//", StringComparison.Ordinal) || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
            return "/";
        return returnUrl;
    }
}
