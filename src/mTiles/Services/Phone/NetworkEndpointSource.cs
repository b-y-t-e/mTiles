using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace mTiles.Services.Phone;

/// <summary>
/// Every usable IPv4 address on this machine's own adapters, classified into <see cref="PhoneEndpointKind"/>.
/// </summary>
/// <remarks>
/// IPv4 only, deliberately. An IPv6 link-local address carries a zone index (<c>fe80::1%14</c>) that no
/// phone browser can be given, and a global IPv6 is rare on the home networks this feature is for — so
/// offering them would add rows to the panel that mostly do not work, which is the opposite of the goal.
/// </remarks>
internal sealed class NetworkEndpointSource : IPhoneEndpointSource
{
    /// <summary>
    /// Adapters that exist only to serve something running on this machine. Named because their addresses
    /// are indistinguishable from a real LAN otherwise — a WSL adapter is a 172.x RFC1918 address like any
    /// other — and offering them puts an address in the QR code that no phone on earth can reach.
    /// </summary>
    /// <remarks>
    /// A backstop, not the main filter. The main filter is <see cref="PhoneEndpoint.HasDefaultGateway"/>,
    /// which needs no list and does not go stale: a virtual switch has no default route because nothing
    /// behind it routes anywhere. This list only demotes what the gateway test would already demote, so a
    /// virtualisation product nobody here has heard of still lands below every real network card.
    /// </remarks>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "hyper-v", "vethernet", "wsl", "docker", "virtualbox", "vmware", "vmnet", "loopback",
    ];

    /// <summary>Adapter names that mean a tunnel, for the platforms where the type does not say so.</summary>
    private static readonly string[] VpnAdapterMarkers =
    [
        "wireguard", "openvpn", "zerotier", "anyconnect", "forticlient", "globalprotect", "nordlynx",
        "wg0", "tun", "tap-windows", "pangp", "vpn",
    ];

    public string Name => "network interfaces";

    public IReadOnlyList<PhoneEndpoint> Discover()
    {
        var found = new List<PhoneEndpoint>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch { continue; }   // an adapter can disappear between the enumeration and this call

            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address is { } address &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !address.Equals(IPAddress.Any));

            foreach (var unicast in properties.UnicastAddresses)
            {
                try
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (IsLinkLocal(address)) continue;   // 169.254.x — DHCP failed, reaches nothing

                    var kind = Classify(adapter, address);
                    if (kind == PhoneEndpointKind.Lan && IsVirtualAdapter(adapter) && !hasGateway)
                        continue;

                    found.Add(new PhoneEndpoint(
                        Host: address.ToString(),
                        Kind: kind,
                        InterfaceName: adapter.Name,
                        Description: adapter.Description,
                        HasDefaultGateway: hasGateway,
                        // A bare IP address can never carry a certificate a browser already trusts. Only the
                        // Tailscale *name* can, which is why that arrives from TailscaleEndpointSource instead.
                        SupportsTrustedCertificate: false));
                }
                catch (Exception ex)
                {
                    // One address, not the machine. An adapter can be torn down while this loop is
                    // walking it, and letting that throw out of the method discarded every address found
                    // so far — leaving the panel with nothing to offer because one virtual switch went
                    // away mid-enumeration.
                    Trace.TraceInformation("An adapter address could not be read: {0}", ex.Message);
                }
            }
        }

        return found;
    }

    /// <summary>169.254.0.0/16 — the address a machine gives itself when DHCP fails.</summary>
    internal static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>100.64.0.0/10, the shared-address space Tailscale hands out.</summary>
    internal static bool IsTailscaleAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    internal static PhoneEndpointKind Classify(NetworkInterface adapter, IPAddress address)
    {
        var tunnel = adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel;

        // The adapter's name is the evidence; the address on its own is not. 100.64.0.0/10 is the shared
        // address space carrier-grade NAT is built from, so a machine behind a mobile hotspot or one of
        // the ISPs that use it has a perfectly ordinary 100.x address on its Wi-Fi card. Calling that
        // Tailscale put a LAN address under "Phone on another network", labelled "reaches your phone from
        // any network" — the one claim it certainly cannot make.
        if (Mentions(adapter, "tailscale") || (IsTailscaleAddress(address) && tunnel))
            return PhoneEndpointKind.Tailscale;

        if (tunnel)
            return PhoneEndpointKind.Vpn;

        return VpnAdapterMarkers.Any(marker => Mentions(adapter, marker))
            ? PhoneEndpointKind.Vpn
            : PhoneEndpointKind.Lan;
    }

    private static bool IsVirtualAdapter(NetworkInterface adapter) =>
        VirtualAdapterMarkers.Any(marker => Mentions(adapter, marker));

    private static bool Mentions(NetworkInterface adapter, string marker) =>
        adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
        adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// This machine's Tailscale MagicDNS name, which is the only endpoint that can be given a certificate the
/// phone's browser already trusts.
/// </summary>
/// <remarks>
/// Worth a source of its own — and worth shelling out for — because of what the name buys. A browser
/// refuses <c>getUserMedia</c> to any page that is not a secure context, and a self-signed certificate on
/// a LAN address only becomes one after the user has read and dismissed a full-page security warning. The
/// MagicDNS name resolves to a real Let's Encrypt certificate through <c>tailscale cert</c>, so the phone
/// asks nothing and simply works. That is the whole reason this is the recommended path for remote work
/// rather than merely one of several.
/// </remarks>
internal sealed class TailscaleEndpointSource(ITailscaleCli? cli = null) : IPhoneEndpointSource
{
    private readonly ITailscaleCli _cli = cli ?? new TailscaleCli();

    public string Name => "tailscale";

    public IReadOnlyList<PhoneEndpoint> Discover()
    {
        if (_cli.GetStatus() is not { } status || string.IsNullOrWhiteSpace(status.MagicDnsName))
            return [];

        return
        [
            new PhoneEndpoint(
                Host: status.MagicDnsName,
                Kind: PhoneEndpointKind.Tailscale,
                InterfaceName: "Tailscale",
                Description: status.HasMobilePeer
                    ? "Tailscale — a phone is signed in to this tailnet"
                    : "Tailscale",
                HasDefaultGateway: true,   // reaches the phone wherever it is; the gateway test is meaningless here
                SupportsTrustedCertificate: true),
        ];
    }
}

/// <summary>What this application needs to know from the Tailscale client.</summary>
/// <param name="MagicDnsName">The machine's MagicDNS name without the trailing dot, or empty.</param>
/// <param name="HasMobilePeer">
/// Whether a phone or tablet is currently signed in to the same tailnet. Not required for anything to
/// work — it only makes the ranking's confidence honest, so "the remote option is the one that will
/// reach your phone" is said when it is known rather than assumed.
/// </param>
internal sealed record TailscaleStatus(string MagicDnsName, bool HasMobilePeer);

/// <summary>Reads the local Tailscale client. An interface so the ranking can be tested without one.</summary>
internal interface ITailscaleCli
{
    /// <summary>The tailnet as this machine sees it, or null when Tailscale is absent or not running.</summary>
    TailscaleStatus? GetStatus();

    /// <summary>
    /// Asks Tailscale for a real certificate for <paramref name="hostName"/>, written to the two paths.
    /// False when Tailscale cannot issue one — HTTPS is not enabled for the tailnet, most often.
    /// </summary>
    bool TryIssueCertificate(string hostName, string certPath, string keyPath);
}

/// <summary>Talks to the <c>tailscale</c> command line.</summary>
/// <remarks>
/// The CLI rather than the LocalAPI socket: the socket's path and its authentication differ between
/// Windows, macOS and every Linux packaging, while <c>tailscale status --json</c> has been stable for
/// years and is what the product documents. Both calls fail soft — no Tailscale simply means the panel
/// shows one QR code instead of two.
/// </remarks>
internal sealed class TailscaleCli : ITailscaleCli
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Issuing a certificate can involve a round trip to Let's Encrypt on the first call.</summary>
    private static readonly TimeSpan CertificateTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long to wait for the output pipes after the process itself has ended.</summary>
    /// <remarks>
    /// Short, because by this point the process is gone and anything still holding the pipe open is a
    /// grandchild that has outlived it. Waiting on that is waiting for something nobody is going to end.
    /// </remarks>
    private static readonly TimeSpan ReadDrainTimeout = TimeSpan.FromSeconds(2);

    public TailscaleStatus? GetStatus()
    {
        if (Run(["status", "--json"], StatusTimeout) is not { } json || json.Length == 0)
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("Self", out var self) ||
                !self.TryGetProperty("DNSName", out var dnsName))
                return null;

            var name = (dnsName.GetString() ?? "").TrimEnd('.');
            if (name.Length == 0)
                return null;

            return new TailscaleStatus(name, HasMobilePeer(root));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Tailscale status could not be parsed: {0}", ex.Message);
            return null;
        }
    }

    public bool TryIssueCertificate(string hostName, string certPath, string keyPath)
    {
        var output = Run(
            ["cert", "--cert-file", certPath, "--key-file", keyPath, hostName],
            CertificateTimeout);

        return output is not null && File.Exists(certPath) && File.Exists(keyPath);
    }

    private static bool HasMobilePeer(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("Peer", out var peers) ||
            peers.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;

        foreach (var peer in peers.EnumerateObject())
        {
            if (!peer.Value.TryGetProperty("OS", out var os)) continue;
            var name = os.GetString();
            if (name is not ("iOS" or "android")) continue;

            // Offline peers say nothing about where the phone is now, and the point of the flag is to
            // tell the user the remote QR code will reach the device in their hand.
            if (peer.Value.TryGetProperty("Online", out var online) && online.ValueKind ==
                System.Text.Json.JsonValueKind.True)
                return true;
        }

        return false;
    }

    private static string? Run(string[] arguments, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            // Read before waiting: a process whose output fills the pipe buffer blocks for ever if the
            // parent is sitting in WaitForExit instead of draining it.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            // Both waits are bounded, and the second one is the point. WaitForExit(int) returns when the
            // *process* ends without waiting for redirected output to finish, so reading .Result after it
            // was an unbounded block: a grandchild inheriting the pipe handle keeps the stream open after
            // its parent has gone, and this runs on the thread the panel is waiting on. A phone panel
            // that never opens, for a program that is not even running any more.
            var reads = Task.WhenAll(stdout, stderr);

            if (!process.WaitForExit((int)timeout.TotalMilliseconds) ||
                !reads.Wait(ReadDrainTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
            {
                Trace.TraceInformation("tailscale {0} exited {1}: {2}",
                    string.Join(' ', arguments), process.ExitCode, stderr.Result.Trim());
                return null;
            }

            return stdout.Result;
        }
        catch (Exception ex)
        {
            // Not installed is the overwhelmingly common case and is not worth a warning.
            Trace.TraceInformation("tailscale could not be run: {0}", ex.Message);
            return null;
        }
    }
}

/// <summary>This machine's mDNS name (<c>host.local</c>).</summary>
/// <remarks>
/// Same reach as a LAN address, but it survives the router handing out a different lease tomorrow, so a
/// QR code photographed once keeps working. Ranked below the numeric addresses because resolution is
/// reliable on iOS and patchy on Android — it is a fallback worth offering, not a default worth betting
/// the first run on.
/// </remarks>
internal sealed class MulticastDnsEndpointSource : IPhoneEndpointSource
{
    public string Name => "mDNS";

    public IReadOnlyList<PhoneEndpoint> Discover()
    {
        string hostName;
        try { hostName = Dns.GetHostName(); }
        catch { return []; }

        // A machine already in a domain answers with its FQDN; appending .local to that produces a name
        // that resolves nowhere.
        var shortName = hostName.Split('.')[0].Trim();
        var candidate = $"{shortName.ToLowerInvariant()}.local";

        // Checked here rather than left to the certificate. A name a certificate cannot carry takes the
        // whole certificate down with it, and this is the only candidate in the panel that comes from
        // something a person typed — the computer's name.
        if (!PhoneHostName.IsUsable(candidate))
            return [];

        return
        [
            new PhoneEndpoint(
                Host: candidate,
                Kind: PhoneEndpointKind.MulticastDns,
                InterfaceName: "mDNS",
                Description: "Name on the local network",
                HasDefaultGateway: false,
                SupportsTrustedCertificate: false),
        ];
    }
}
