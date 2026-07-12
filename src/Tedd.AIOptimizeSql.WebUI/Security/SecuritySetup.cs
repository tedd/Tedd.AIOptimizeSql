using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

using Tedd.AIOptimizeSql.WebUI.Options;

namespace Tedd.AIOptimizeSql.WebUI.Security;

/// <summary>
/// Effective security posture resolved at startup, registered as a singleton so UI
/// components (e.g. the logout button) can branch on it without re-reading configuration.
/// </summary>
public sealed record SecurityState(bool AuthenticationEnabled, bool IpFilterEnabled);

public static class SecuritySetup
{
    public const string CookieName = ".AIOptimizeSql.Auth";

    /// <summary>Loopback-only default so a fresh local install is not exposed to the network.</summary>
    public const string DefaultLocalUrl = "http://127.0.0.1:5000";

    public static bool IsAzureAppService
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));

    private static bool IsContainer
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Binds <see cref="SecurityOptions"/>, applies the loopback-only default listen address,
    /// and registers cookie authentication when required. Fails fast when authentication is
    /// required but no credentials are configured.
    /// </summary>
    public static SecurityState AddAIOptimizeSecurity(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        builder.Services.AddSingleton(options);

        ApplyDefaultListenAddress(builder);

        var authenticationEnabled = ResolveAuthenticationEnabled(options.Authentication.Mode, IsAzureAppService);
        if (authenticationEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.Authentication.Username) || string.IsNullOrEmpty(options.Authentication.Password))
                throw new InvalidOperationException(
                    "Authentication is required (Security:Authentication:Mode is 'Enabled', or 'Auto' while running on Azure App Service) " +
                    "but no credentials are configured. Set Security:Authentication:Username and Security:Authentication:Password " +
                    "(as Azure App Service settings: Security__Authentication__Username / Security__Authentication__Password), " +
                    "or set Security:Authentication:Mode=Disabled to run without authentication.");

            builder.Services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookie =>
                {
                    cookie.Cookie.Name = CookieName;
                    cookie.Cookie.HttpOnly = true;
                    cookie.LoginPath = "/login";
                    cookie.ReturnUrlParameter = "returnUrl";
                    cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                    cookie.SlidingExpiration = true;
                });
            builder.Services.AddAuthorization();
        }

        var state = new SecurityState(authenticationEnabled, options.AllowedRemoteIPs.Length > 0);
        builder.Services.AddSingleton(state);
        return state;
    }

    /// <summary>
    /// Forwarded-headers processing (Azure App Service sits behind a reverse proxy) and the
    /// remote-IP allowlist. Must be registered before any middleware that inspects the
    /// request scheme or client address.
    /// </summary>
    public static WebApplication UseAIOptimizeSecurity(this WebApplication app, SecurityState state)
    {
        // App Service terminates TLS and proxies over HTTP; without this the app would see
        // scheme=http and the proxy's address instead of the real client. ForwardLimit=1
        // (the default) only honors the entry appended by the closest hop — the App Service
        // front end — so clients cannot spoof X-Forwarded-For. Skipped when the standard
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED switch already inserted the middleware.
        if (IsAzureAppService && !app.Configuration.GetValue<bool>("FORWARDEDHEADERS_ENABLED"))
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            // Front-end addresses vary per scale unit; trust the immediate hop instead of a fixed list.
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }

        var options = app.Services.GetRequiredService<SecurityOptions>();
        if (state.IpFilterEnabled)
        {
            RemoteIpAllowList allowList;
            try
            {
                allowList = RemoteIpAllowList.Parse(options.AllowedRemoteIPs);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            app.UseMiddleware<RemoteIpFilterMiddleware>(allowList);
        }

        if (state.AuthenticationEnabled && options.Authentication.Password.Length < 12)
            app.Logger.LogWarning("The configured Security:Authentication:Password is shorter than 12 characters; consider a longer password.");

        app.Logger.LogInformation(
            "Security: authentication {AuthState} (Mode={Mode}{AzureHint}), remote IP allowlist {IpState}.",
            state.AuthenticationEnabled ? "enabled" : "disabled",
            options.Authentication.Mode,
            options.Authentication.Mode == SecurityAuthenticationMode.Auto
                ? IsAzureAppService ? ", Azure App Service detected" : ", local host"
                : string.Empty,
            state.IpFilterEnabled ? $"active with {options.AllowedRemoteIPs.Length} entries" : "not configured");

        return app;
    }

    public static bool ResolveAuthenticationEnabled(SecurityAuthenticationMode mode, bool isAzureAppService) => mode switch
    {
        SecurityAuthenticationMode.Enabled => true,
        SecurityAuthenticationMode.Disabled => false,
        _ => isAzureAppService
    };

    /// <summary>
    /// Username is compared case-insensitively; the password comparison is fixed-time.
    /// Both are always evaluated so a valid username is not revealed through timing.
    /// </summary>
    public static bool ValidateCredentials(SecurityAuthenticationOptions configured, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(configured.Username) || string.IsNullOrEmpty(configured.Password))
            return false;

        var usernameMatches = string.Equals(configured.Username, username ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(configured.Password.AsSpan()),
            MemoryMarshal.AsBytes((password ?? string.Empty).AsSpan()));

        return usernameMatches & passwordMatches;
    }

    /// <summary>
    /// Default to loopback-only listening when nothing else configures the listen address.
    /// Explicit configuration (Urls / ASPNETCORE_URLS / --urls, HTTP_PORTS, a Kestrel
    /// endpoints section) always wins, and hosts that inject their own binding (Azure App
    /// Service, containers) are left untouched.
    /// </summary>
    private static void ApplyDefaultListenAddress(WebApplicationBuilder builder)
    {
        if (IsAzureAppService || IsContainer)
            return;
        if (!string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
            return;
        if (!string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.HttpPortsKey])
            || !string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.HttpsPortsKey]))
            return;
        if (builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
            return;

        builder.WebHost.UseUrls(DefaultLocalUrl);
    }
}
