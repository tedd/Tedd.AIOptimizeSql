namespace Tedd.AIOptimizeSql.WebUI.Options;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public SecurityAuthenticationOptions Authentication { get; set; } = new();

    /// <summary>
    /// Optional remote-IP allowlist. Empty (the default) admits every address. Entries are
    /// single addresses ("203.0.113.7", "2001:db8::1") or CIDR ranges ("10.0.0.0/8").
    /// Loopback is always admitted so a local operator can never lock themselves out.
    /// </summary>
    public string[] AllowedRemoteIPs { get; set; } = [];
}

public sealed class SecurityAuthenticationOptions
{
    /// <summary>
    /// Auto (default): authentication is required on Azure App Service and disabled
    /// everywhere else. Enabled/Disabled force it on or off regardless of host.
    /// </summary>
    public SecurityAuthenticationMode Mode { get; set; } = SecurityAuthenticationMode.Auto;

    /// <summary>Username of the single account (compared case-insensitively).</summary>
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public enum SecurityAuthenticationMode
{
    Auto,
    Enabled,
    Disabled
}
