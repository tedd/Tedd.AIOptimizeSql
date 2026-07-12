using System.Net;

namespace Tedd.AIOptimizeSql.WebUI.Security;

/// <summary>
/// Immutable matcher for the <c>Security:AllowedRemoteIPs</c> setting. Entries are single
/// addresses or CIDR ranges; loopback is always allowed so the operator cannot lock
/// themselves out of a box they are logged in to.
/// </summary>
public sealed class RemoteIpAllowList
{
    private readonly IPNetwork[] _networks;

    private RemoteIpAllowList(IPNetwork[] networks) => _networks = networks;

    public static RemoteIpAllowList Empty { get; } = new([]);

    public bool IsEmpty => _networks.Length == 0;

    /// <exception cref="FormatException">An entry is neither an IP address nor a CIDR range.</exception>
    public static RemoteIpAllowList Parse(IEnumerable<string> entries)
    {
        var networks = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
                continue;
            }

            if (IPAddress.TryParse(entry, out var address))
            {
                address = Normalize(address);
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
                continue;
            }

            throw new FormatException(
                $"Invalid Security:AllowedRemoteIPs entry '{entry}'. Use a single IP address (\"203.0.113.7\") " +
                "or a CIDR range (\"192.168.1.0/24\").");
        }

        return networks.Count == 0 ? Empty : new RemoteIpAllowList([.. networks]);
    }

    public bool IsAllowed(IPAddress address)
    {
        address = Normalize(address);

        if (IPAddress.IsLoopback(address))
            return true;

        foreach (var network in _networks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }

    /// <summary>Kestrel reports IPv4 clients on dual-stack sockets as ::ffff:a.b.c.d.</summary>
    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
