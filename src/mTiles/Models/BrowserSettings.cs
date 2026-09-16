namespace mTiles.Models;

/// <summary>The browser tile, and the relay that lets another machine's browser tile out through this one.</summary>
public sealed class BrowserSettings
{
    /// <summary>Where a new browser tile opens, and where its Home button goes.</summary>
    public string HomePage { get; set; } = "https://www.google.com";

    /// <summary>
    /// The proxy every browser tile's traffic goes through, or empty for a direct connection.
    /// </summary>
    /// <remarks>
    /// <para>Typed as the engine takes it — <c>socks5://host:port</c> or <c>http://host:port</c>, a bare
    /// <c>host:port</c> meaning HTTP — and checked by <c>BrowserProxy</c> before it reaches a command line.
    /// The usual value is another machine's relay (<see cref="RelayEnabled"/>) at its Tailscale address,
    /// so the tile goes out through that machine's network: the name is resolved and the connection is
    /// made at the far end.</para>
    /// <para>Windows only. WebView2 takes it as a browser argument; WebKitGTK has no per-view equivalent
    /// here, so on Linux the tile follows the system proxy and this field is ignored.</para>
    /// </remarks>
    public string ProxyServer { get; set; } = "";

    /// <summary>Whether this machine accepts browser traffic from other machines on its tailnet.</summary>
    /// <remarks>
    /// Off by default, and that is the posture rather than a preference: it is a proxy, and a proxy is a
    /// way into whatever network this machine sits on. It listens on this machine's Tailscale address
    /// only and answers nobody outside 100.64.0.0/10, so what can use it is what Tailscale has already
    /// let onto the tailnet — see <c>BrowserRelay</c>.
    /// </remarks>
    public bool RelayEnabled { get; set; }

    /// <summary>The port the relay listens on.</summary>
    public int RelayPort { get; set; } = 18093;

    /// <summary>
    /// Whether the browser resolves names itself over an encrypted connection (DNS over HTTPS).
    /// </summary>
    /// <remarks>
    /// <para>The point of it is a network that filters by refusing to resolve a name, or by handing back a
    /// substitute address — measured 2026-09-16, a resolver that answered <c>www.youtube.com</c> with
    /// Google's Restricted-Mode address. With this on, the browser asks <see cref="SecureDnsTemplate"/>
    /// directly and never sees that answer.</para>
    /// <para>Windows only, and its own switch rather than folded into the proxy, because the two solve
    /// different halves: a proxy sends the whole connection out through another machine, this only changes
    /// where names are looked up while the connection still leaves from here. It does nothing against a
    /// network that blocks by address or by the name in the TLS handshake — for that the proxy is the
    /// answer.</para>
    /// </remarks>
    public bool SecureDns { get; set; }

    /// <summary>The DNS-over-HTTPS resolver the browser uses when <see cref="SecureDns"/> is on.</summary>
    public string SecureDnsTemplate { get; set; } = "https://cloudflare-dns.com/dns-query";
}
