using System.Net;

using Tedd.AIOptimizeSql.WebUI.Options;
using Tedd.AIOptimizeSql.WebUI.Security;

namespace Tedd.AIOptimizeSql.Tests;

public class SecuritySetupTests
{
    [Theory]
    [InlineData(SecurityAuthenticationMode.Auto, true, true)]
    [InlineData(SecurityAuthenticationMode.Auto, false, false)]
    [InlineData(SecurityAuthenticationMode.Enabled, true, true)]
    [InlineData(SecurityAuthenticationMode.Enabled, false, true)]
    [InlineData(SecurityAuthenticationMode.Disabled, true, false)]
    [InlineData(SecurityAuthenticationMode.Disabled, false, false)]
    public void Auto_mode_requires_authentication_only_on_azure(SecurityAuthenticationMode mode, bool isAzure, bool expected)
    {
        Assert.Equal(expected, SecuritySetup.ResolveAuthenticationEnabled(mode, isAzure));
    }

    private static SecurityAuthenticationOptions Configured(string username = "admin", string password = "s3cret-pass")
        => new() { Username = username, Password = password };

    [Fact]
    public void Correct_credentials_validate()
    {
        Assert.True(SecuritySetup.ValidateCredentials(Configured(), "admin", "s3cret-pass"));
    }

    [Fact]
    public void Username_is_case_insensitive()
    {
        Assert.True(SecuritySetup.ValidateCredentials(Configured(), "ADMIN", "s3cret-pass"));
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("admin", "")]
    [InlineData("admin", null)]
    [InlineData("someone", "s3cret-pass")]
    [InlineData("", "s3cret-pass")]
    [InlineData(null, null)]
    public void Wrong_credentials_are_rejected(string? username, string? password)
    {
        Assert.False(SecuritySetup.ValidateCredentials(Configured(), username, password));
    }

    [Fact]
    public void Unconfigured_account_rejects_everything_including_empty_input()
    {
        var unconfigured = new SecurityAuthenticationOptions();
        Assert.False(SecuritySetup.ValidateCredentials(unconfigured, "", ""));
        Assert.False(SecuritySetup.ValidateCredentials(unconfigured, null, null));
    }
}

public class RemoteIpAllowListTests
{
    [Fact]
    public void Empty_list_reports_empty()
    {
        Assert.True(RemoteIpAllowList.Parse([]).IsEmpty);
        Assert.True(RemoteIpAllowList.Parse(["", "  "]).IsEmpty);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("10.0.0.0/8", "10.42.1.2", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("192.168.1.5/24", "192.168.1.200", true)] // host bits are normalized away

    [InlineData("2001:db8::/32", "2001:db8::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::5", "2001:db8::5", true)]
    public void Matches_single_addresses_and_cidr_ranges(string entry, string candidate, bool expected)
    {
        var list = RemoteIpAllowList.Parse([entry]);
        Assert.Equal(expected, list.IsAllowed(IPAddress.Parse(candidate)));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_clients_match_ipv4_entries()
    {
        // Kestrel reports IPv4 clients on dual-stack sockets as ::ffff:a.b.c.d.
        var list = RemoteIpAllowList.Parse(["192.168.1.0/24"]);
        Assert.True(list.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10")));
        Assert.False(list.IsAllowed(IPAddress.Parse("::ffff:192.168.2.10")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_is_always_allowed(string loopback)
    {
        var list = RemoteIpAllowList.Parse(["203.0.113.7"]);
        Assert.True(list.IsAllowed(IPAddress.Parse(loopback)));
    }

    [Fact]
    public void Non_loopback_is_rejected_when_not_listed()
    {
        var list = RemoteIpAllowList.Parse(["203.0.113.7"]);
        Assert.False(list.IsAllowed(IPAddress.Parse("198.51.100.23")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    public void Invalid_entries_throw_with_the_offending_entry(string entry)
    {
        var ex = Assert.Throws<FormatException>(() => RemoteIpAllowList.Parse([entry]));
        Assert.Contains(entry, ex.Message);
    }

    [Fact]
    public void Multiple_entries_are_all_honored()
    {
        var list = RemoteIpAllowList.Parse(["203.0.113.7", "10.0.0.0/8"]);
        Assert.True(list.IsAllowed(IPAddress.Parse("203.0.113.7")));
        Assert.True(list.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.False(list.IsAllowed(IPAddress.Parse("203.0.113.8")));
    }
}
