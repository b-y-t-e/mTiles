using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mTiles.Services.Browser;

/// <summary>A request the relay was asked to carry: where to, and what to send first.</summary>
/// <param name="Host">The host to connect to.</param>
/// <param name="Port">The port to connect to.</param>
/// <param name="IsTunnel">A <c>CONNECT</c>: answered with 200 and then carried byte for byte.</param>
/// <param name="Forward">For a plain request, the head rewritten for the origin server; empty for a tunnel.</param>
public sealed record RelayRequest(string Host, int Port, bool IsTunnel, byte[] Forward);

/// <summary>The pure half of <see cref="BrowserRelay"/>: who may use it, where it may go, and how a
/// request head is read.</summary>
/// <remarks>Separate from the sockets so each rule is a table test, because every one of them is a
/// security rule and none of them can be watched working from the outside.</remarks>
public static class RelayRules
{
    /// <summary>The largest request head accepted. A browser's is a few hundred bytes.</summary>
    public const int MaxHeadBytes = 16 * 1024;

    /// <summary>Whether <paramref name="address"/> is on a tailnet: 100.64.0.0/10, or Tailscale's own
    /// IPv6 prefix fd7a:115c:a1e0::/48.</summary>
    /// <remarks>The only peers the relay answers. Tailscale has already decided who may reach this
    /// machine at such an address; anything else arriving on the port is refused before a byte is read.
    /// </remarks>
    public static bool IsTailnet(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 100 && bytes[1] is >= 64 and <= 127,
            AddressFamily.InterNetworkV6 => bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11
                                            && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0,
            _ => false,
        };
    }

    /// <summary>Whether the relay may connect to <paramref name="address"/>: the public internet only.</summary>
    /// <remarks>
    /// <para>This is what keeps the relay a way <em>out</em> rather than a way <em>in</em>. Loopback
    /// matters most: this machine's database bridge listens on localhost and trusts a request for
    /// having arrived there, so a relay that would connect to 127.0.0.1 hands every tailnet peer the
    /// databases. The private ranges, link-local and the tailnet itself are refused for the same reason
    /// one step out — the network this machine sits on is not what was offered.</para>
    /// <para>Asked of the <em>resolved</em> address, never of the name: a public name can resolve to
    /// 127.0.0.1, and that is the whole of a rebinding attack.</para>
    /// </remarks>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || IsTailnet(address))
            return false;

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 0
                     || b[0] == 10
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || b[0] >= 224);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.Equals(IPAddress.IPv6None)
                     || address.IsIPv6LinkLocal
                     || address.IsIPv6SiteLocal
                     || address.IsIPv6Multicast
                     || (b[0] & 0xfe) == 0xfc);

        return false;
    }

    /// <summary>Reads a request head (everything up to and including the blank line), or null when it
    /// is not one this relay carries.</summary>
    public static RelayRequest? Parse(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head);
        var lines = text.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var (method, target, version) = (start[0], start[1], start[2]);

        if (method == "CONNECT")
            return SplitHostPort(target, defaultPort: null) is var (host, port)
                ? new RelayRequest(host, port, true, [])
                : null;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host.Length == 0)
            return null;

        var forward = new StringBuilder()
            .Append(method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                continue;
            var name = line.Split(':', 2)[0].Trim();
            if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                continue;
            forward.Append(line).Append("\r\n");
        }
        // One request per connection: the relay does not read responses, so it cannot tell where a
        // second request on the same connection would begin.
        forward.Append("Connection: close\r\n\r\n");

        return new RelayRequest(uri.IdnHost, uri.Port, false, Encoding.Latin1.GetBytes(forward.ToString()));
    }

    /// <summary>Where the head ends in <paramref name="buffer"/>, or -1 while it has not.</summary>
    public static int HeadLength(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf("\r\n\r\n"u8);
        return end < 0 ? -1 : end + 4;
    }

    private static (string Host, int Port)? SplitHostPort(string authority, int? defaultPort)
    {
        var colon = authority.LastIndexOf(':');
        var bracket = authority.LastIndexOf(']');
        string host;
        int port;
        if (colon > bracket && colon > 0)
        {
            host = authority[..colon];
            if (!int.TryParse(authority[(colon + 1)..], out port) || port is < 1 or > 65535)
                return null;
        }
        else if (defaultPort is { } fallback)
        {
            host = authority;
            port = fallback;
        }
        else
        {
            return null;
        }

        host = host.Trim('[', ']');
        return host.Length == 0 || host.Contains(' ') ? null : (host, port);
    }
}
