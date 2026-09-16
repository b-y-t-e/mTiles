using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using mTiles.Models;

namespace mTiles.Services.Browser;

/// <summary>
/// A small HTTP proxy that lets a browser tile on another machine reach the internet through this one,
/// over Tailscale.
/// </summary>
/// <remarks>
/// <para><b>What it is.</b> A browser tile pointed at this relay (<c>BrowserSettings.ProxyServer</c>)
/// has its names resolved and its connections made from here. It is the same effect as using this
/// machine as a Tailscale exit node, confined to the browser tiles — the rest of that machine keeps its
/// own network.</para>
/// <para><b>Who may use it</b> is decided by <see cref="RelayRules"/>: it listens on this machine's
/// tailnet addresses only, answers peers on the tailnet only, and connects to the public internet only.
/// HTTPS is carried as an opaque tunnel (<c>CONNECT</c>), so nothing here ever sees a page.</para>
/// <para>Started and stopped from settings (<see cref="Apply"/>), off by default, and never kept alive
/// past the application.</para>
/// </remarks>
public sealed class BrowserRelay : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings;
    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;
    private (bool Enabled, int Port) _applied;

    public BrowserRelay(SettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += Apply;
        Apply();
    }

    /// <summary>Raised when <see cref="Status"/> or <see cref="Addresses"/> change. Any thread.</summary>
    public event Action? Changed;

    /// <summary>One sentence about the relay, for the settings page.</summary>
    public string Status { get; private set; } = "Off.";

    /// <summary>What another machine types into its proxy field, one per tailnet address.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    /// <summary>Brings the relay in line with the settings. Cheap when nothing it reads has moved.</summary>
    public void Apply() => Apply(force: false);

    /// <summary>Starts again from scratch — for the settings page's Retry, since Tailscale connecting
    /// after the relay gave up is not something anything here is told about.</summary>
    public void Restart() => Apply(force: true);

    private void Apply(bool force)
    {
        var browser = _settings.Settings.Browser;
        var wanted = (browser.RelayEnabled, browser.RelayPort);
        if (!force && wanted == _applied)
            return;

        _applied = wanted;
        Stop();

        if (!wanted.RelayEnabled)
        {
            Publish("Off.", []);
            return;
        }

        var local = TailnetAddresses();
        if (local.Count == 0)
        {
            Publish("No Tailscale address on this machine - is Tailscale connected?", []);
            return;
        }

        _cts = new CancellationTokenSource();
        var addresses = new List<string>();
        string? failure = null;
        foreach (var address in local)
        {
            try
            {
                var listener = new TcpListener(address, wanted.RelayPort);
                listener.Start();
                _listeners.Add(listener);
                addresses.Add(address.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"http://[{address}]:{wanted.RelayPort}"
                    : $"http://{address}:{wanted.RelayPort}");
                _ = AcceptLoopAsync(listener, _cts.Token);
            }
            catch (SocketException ex)
            {
                failure = $"Port {wanted.RelayPort} could not be opened: {ex.Message}";
                Trace.TraceWarning("Browser relay: listening on {0}:{1} failed: {2}", address, wanted.RelayPort, ex.Message);
            }
        }

        Publish(addresses.Count > 0 ? "Listening on Tailscale." : failure ?? "Could not listen.", addresses);
    }

    private void Publish(string status, IReadOnlyList<string> addresses)
    {
        Status = status;
        Addresses = addresses;
        Changed?.Invoke();
    }

    internal static List<IPAddress> TailnetAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(RelayRules.IsTailnet)
                // IPv4 first: it is the address people type.
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch (NetworkInformationException ex)
        {
            Trace.TraceWarning("Browser relay: listing network adapters failed: {0}", ex.Message);
            return [];
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                Trace.TraceWarning("Browser relay: accepting stopped: {0}", ex.Message);
                return;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private static async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || !RelayRules.IsTailnet(peer.Address))
                return;

            client.NoDelay = true;
            var downstream = client.GetStream();

            var buffer = new byte[RelayRules.MaxHeadBytes];
            var filled = 0;
            int headLength;
            using (var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headTimeout.CancelAfter(ConnectTimeout);
                while ((headLength = RelayRules.HeadLength(buffer.AsSpan(0, filled))) < 0)
                {
                    if (filled == buffer.Length)
                        return;
                    var read = await downstream.ReadAsync(buffer.AsMemory(filled), headTimeout.Token);
                    if (read == 0)
                        return;
                    filled += read;
                }
            }

            if (RelayRules.Parse(buffer.AsSpan(0, headLength)) is not { } request)
            {
                await ReplyAsync(downstream, "400 Bad Request", ct);
                return;
            }

            using var upstreamClient = await ConnectAsync(request.Host, request.Port, ct);
            if (upstreamClient is null)
            {
                await ReplyAsync(downstream, "502 Bad Gateway", ct);
                return;
            }

            upstreamClient.NoDelay = true;
            var upstream = upstreamClient.GetStream();

            if (request.IsTunnel)
                await ReplyAsync(downstream, "200 Connection Established", ct);
            else
                await upstream.WriteAsync(request.Forward, ct);

            // Whatever the browser sent after the head (a TLS hello that arrived in the same packet, or a
            // request body) goes on before the two streams are joined.
            if (filled > headLength)
                await upstream.WriteAsync(buffer.AsMemory(headLength, filled - headLength), ct);

            using var pipe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = PumpAsync(downstream, upstreamClient, upstream, pipe.Token);
            var down = PumpAsync(upstream, client, downstream, pipe.Token);
            await Task.WhenAny(up, down);
            // One side has finished; give the other a moment to deliver what it already has.
            pipe.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.WhenAll(up, down);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A browser closing a tab mid-download ends here, which is the ordinary case.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Browser relay: a connection failed: {0}", ex);
        }
    }

    private static async Task PumpAsync(Stream from, TcpClient toClient, Stream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, 64 * 1024, ct);
            toClient.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Connects to the first public address the name resolves to, or answers null.</summary>
    private static async Task<TcpClient?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }

        foreach (var address in addresses.Where(RelayRules.IsPublic))
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, port, timeout.Token);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (ct.IsCancellationRequested)
                    return null;
            }
        }

        return null;
    }

    private static async Task ReplyAsync(Stream stream, string status, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); }
            catch (SocketException) { }
        }
        _listeners.Clear();
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= Apply;
        Stop();
    }
}
