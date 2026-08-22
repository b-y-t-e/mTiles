namespace mTiles.Services.Phone;

/// <summary>What kind of network a candidate address is reachable on.</summary>
/// <remarks>
/// The kind is what the ranking reasons about, so it is deliberately about *reach* rather than about the
/// technology: a WireGuard tunnel and a Tailscale one are told apart only because Tailscale can also hand
/// out a real certificate, which is the one difference the user ever sees.
/// </remarks>
internal enum PhoneEndpointKind
{
    /// <summary>An address on a physical adapter — the phone must be on the same network to reach it.</summary>
    Lan,

    /// <summary>A Tailscale address or MagicDNS name. Reachable from anywhere the phone is signed in.</summary>
    Tailscale,

    /// <summary>Some other tunnel (WireGuard, OpenVPN, ZeroTier, a corporate client).</summary>
    Vpn,

    /// <summary>An mDNS name (<c>host.local</c>). Same reach as <see cref="Lan"/>, but survives a DHCP change.</summary>
    MulticastDns,
}

/// <summary>
/// Where the phone has to be for an endpoint to work. The panel shows one QR code per audience, so this
/// is what decides which of the two a candidate belongs under.
/// </summary>
/// <remarks>
/// Two, not more, and that is the entire reason the guess is survivable: the ranking can be wrong about
/// *which* LAN address is right, but it cannot be wrong in a way that hides the whole remote option, so
/// a user whose top pick fails always has the other class already on screen.
/// </remarks>
internal enum PhoneEndpointAudience
{
    /// <summary>The phone is on the same Wi-Fi as this machine.</summary>
    SameNetwork,

    /// <summary>The phone is somewhere else and reaches this machine through a tunnel.</summary>
    Remote,
}

/// <summary>
/// One address this machine could be reached at, as discovered by an <see cref="IPhoneEndpointSource"/>.
/// </summary>
/// <param name="Host">
/// What goes in the URL — a bare IPv4 address, or a name. Never a bracketed IPv6 literal with a zone id:
/// a link-local address cannot be typed into a phone browser, so those are dropped at the source.
/// </param>
/// <param name="Kind">Which network this is on. Drives both the audience and the ranking.</param>
/// <param name="InterfaceName">The adapter it came from, shown to the user so two LAN addresses can be told apart.</param>
/// <param name="Description">The adapter's human description, used when the name is a GUID-like nothing.</param>
/// <param name="HasDefaultGateway">
/// Whether the adapter has a default route. This single fact is what separates a real network card from
/// the pile of virtual ones a developer machine carries — Hyper-V, WSL, Docker and VirtualBox adapters
/// have no gateway — and it does it without a name list that goes stale the moment a new tool ships.
/// </param>
/// <param name="SupportsTrustedCertificate">
/// Whether a browser can be given a certificate it already trusts for this host. True only for Tailscale
/// MagicDNS names, and load-bearing rather than cosmetic: without a trusted certificate the phone's
/// browser refuses the page a microphone outright, so this is the difference between "works" and "works
/// after the user clicks through a security warning".
/// </param>
internal sealed record PhoneEndpoint(
    string Host,
    PhoneEndpointKind Kind,
    string InterfaceName,
    string Description,
    bool HasDefaultGateway,
    bool SupportsTrustedCertificate)
{
    /// <summary>Where the phone must be for this to work.</summary>
    public PhoneEndpointAudience Audience => Kind switch
    {
        PhoneEndpointKind.Tailscale or PhoneEndpointKind.Vpn => PhoneEndpointAudience.Remote,
        _ => PhoneEndpointAudience.SameNetwork,
    };

    /// <summary>
    /// The adapter as it should be named on screen.
    /// </summary>
    /// <remarks>
    /// The name first, which is what <see cref="Description"/>'s own documentation above says — the code
    /// preferred the description regardless, so a card the operating system calls "Wi-Fi" was announced
    /// as "MediaTek Wi-Fi 6E MT7922 (RZ616) 160MHz Wireless LAN Card", four wrapped lines in a card whose
    /// job is to be read at a glance. The description is the fallback it was always meant to be.
    /// </remarks>
    public string AdapterLabel =>
        !string.IsNullOrWhiteSpace(InterfaceName) ? InterfaceName :
        !string.IsNullOrWhiteSpace(Description) ? Description : Host;
}

/// <summary>
/// Whether a name can be put in a certificate and in a URL.
/// </summary>
/// <remarks>
/// <b>Not about non-ASCII.</b> Measured rather than assumed: <c>SubjectAlternativeNameBuilder.AddDnsName</c>
/// accepts <c>andrzej-łaptop.local</c> and <c>żółw.local</c> quite happily — .NET punycodes them through
/// IDN — so a Polish machine name is not the hazard it looks like. What it rejects is a label that is
/// empty, longer than 63 characters, or starts or ends with a hyphen.
/// <para>The consequence of letting one through is out of all proportion to the cause: the throw happens
/// while the certificate is being built, so the <em>whole</em> certificate fails, and with it the bridge —
/// on a machine whose LAN addresses were all perfectly usable. One badly-named computer would have turned
/// the feature off entirely, with "no TLS certificate could be obtained" as the only clue.</para>
/// </remarks>
internal static class PhoneHostName
{
    public static bool IsUsable(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            return false;

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63)
                return false;

            if (label.StartsWith('-') || label.EndsWith('-'))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Somewhere this application might be reachable from a phone. One implementation per kind of network.
/// </summary>
/// <remarks>
/// The extension point of the whole feature. Supporting a new way of reaching the machine — a reverse
/// tunnel, a mesh VPN with its own naming, an SSH forward — is a new source added to the list in
/// <see cref="PhoneEndpointDirectory"/>; neither the ranking nor the server nor the UI changes, because
/// none of them knows what kinds exist beyond the two audiences above.
/// </remarks>
internal interface IPhoneEndpointSource
{
    /// <summary>Name of this source, for the log when it throws.</summary>
    string Name { get; }

    /// <summary>
    /// Everything this source can currently see. Must not throw: a source that cannot answer returns
    /// nothing, because one broken lookup must not cost the user every other address on the machine.
    /// </summary>
    IReadOnlyList<PhoneEndpoint> Discover();
}
