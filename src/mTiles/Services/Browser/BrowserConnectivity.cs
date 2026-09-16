using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace mTiles.Services.Browser;

/// <summary>One line of a check: whether it passed, and what to say.</summary>
public sealed record ConnectivityLine(bool Ok, string Text);

/// <summary>Asks whether a proxy works, and whether this machine's relay is set up to be one.</summary>
/// <remarks>Two steps for a proxy, because the two failures have different fixes: a relay that cannot be
/// reached at all is Tailscale or the far machine's firewall, while one that answers and cannot fetch a
/// page is the far machine's own connection.</remarks>
public static class BrowserConnectivity
{
    /// <summary>A page that answers 204 and nothing else — the cheapest real request there is.</summary>
    internal static readonly Uri Probe = new("https://www.google.com/generate_204");

    private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Tries a request through <paramref name="setting"/>.</summary>
    public static async Task<ConnectivityLine> TestProxyAsync(string? setting)
    {
        var proxy = BrowserProxy.Normalise(setting, out var problem);
        if (proxy is null)
            return new(false, problem ?? "No proxy is set - browser tiles connect directly.");

        var uri = new Uri(proxy);
        try
        {
            using var reach = new CancellationTokenSource(ReachTimeout);
            using var socket = new TcpClient();
            await socket.ConnectAsync(uri.Host.Trim('[', ']'), uri.Port, reach.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new(false, $"Cannot reach {uri.Authority}. Check that Tailscale is connected on both "
                + "machines, that the relay is switched on over there, and that its firewall lets you in "
                + "(Check on that machine says which).");
        }

        var clock = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy(uri), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = FetchTimeout };
            using var response = await http.GetAsync(Probe);
            return response.IsSuccessStatusCode
                ? new(true, $"Connected through {uri.Authority} ({clock.ElapsedMilliseconds} ms).")
                : new(false, $"{uri.Authority} answered, but the test page returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"{uri.Authority} is reachable, but no page came back through it "
                + $"({ex.Message}). The machine sharing its connection may be offline or blocked itself.");
        }
    }

    /// <summary>Everything that has to be true for another machine to go out through this one.</summary>
    public static async Task<IReadOnlyList<ConnectivityLine>> CheckRelayAsync(BrowserRelay relay, int port, bool enabled)
    {
        var lines = new List<ConnectivityLine>();
        if (!enabled)
        {
            lines.Add(new(false, "Sharing is switched off."));
            return lines;
        }

        var tailnet = BrowserRelay.TailnetAddresses();
        lines.Add(tailnet.Count > 0
            ? new(true, $"Tailscale: connected as {tailnet[0]}.")
            : new(false, "Tailscale: no Tailscale address on this machine - start Tailscale and sign in."));

        if (relay.Addresses.Count == 0)
        {
            if (tailnet.Count > 0)
                relay.Restart();
            if (relay.Addresses.Count == 0)
            {
                lines.Add(new(false, $"Relay: not listening - {relay.Status}"));
                return lines;
            }
        }
        lines.Add(new(true, $"Relay: listening on port {port}."));

        var through = await TestProxyAsync(relay.Addresses[0]);
        lines.Add(through.Ok
            ? new(true, "Internet through the relay: works.")
            : new(false, "Internet through the relay: " + through.Text));

        var (ok, message) = await RelayFirewall.CheckAsync(port);
        lines.Add(new(ok, message));
        return lines;
    }
}
