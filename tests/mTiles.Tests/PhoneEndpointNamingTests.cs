using System.Net;
using System.Net.NetworkInformation;
using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What may be put in a certificate, and what an address on its own is evidence of.
/// </summary>
/// <remarks>
/// Both of these decide something the user sees and cannot argue with: whether the bridge starts at all,
/// and which of the two QR codes an address is offered under.
/// </remarks>
public class PhoneEndpointNamingTests
{
    /// <summary>
    /// Non-ASCII is not the hazard it looks like.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed, and the measurement contradicted the assumption:
    /// <c>SubjectAlternativeNameBuilder</c> punycodes these through IDN and accepts them. Pinned here so
    /// nobody "fixes" it later by stripping accents from Polish machine names for no reason.
    /// </remarks>
    [Theory]
    [InlineData("andrzej-łaptop.local")]
    [InlineData("żółw.local")]
    [InlineData("plain-host.local")]
    [InlineData("my_host.local")]
    public void A_name_with_accents_is_usable(string host) =>
        Assert.True(PhoneHostName.IsUsable(host));

    /// <summary>
    /// The shapes that really do throw when a certificate is built.
    /// </summary>
    /// <remarks>
    /// The cost of one of these reaching the builder is out of all proportion: the throw fails the whole
    /// certificate, so a machine whose LAN addresses were all fine loses the feature entirely, with "no
    /// TLS certificate could be obtained" as the only clue.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".local")]
    [InlineData("a..b.local")]
    [InlineData("-leading.local")]
    [InlineData("trailing-.local")]
    public void A_name_a_certificate_cannot_carry_is_refused(string host) =>
        Assert.False(PhoneHostName.IsUsable(host));

    [Fact]
    public void A_label_longer_than_sixty_three_characters_is_refused() =>
        Assert.False(PhoneHostName.IsUsable(new string('x', 64) + ".local"));

    // ── what a 100.x address means ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shared_address_range_is_recognised()
    {
        Assert.True(NetworkEndpointSource.IsTailscaleAddress(IPAddress.Parse("100.64.0.1")));
        Assert.True(NetworkEndpointSource.IsTailscaleAddress(IPAddress.Parse("100.127.255.254")));
        Assert.False(NetworkEndpointSource.IsTailscaleAddress(IPAddress.Parse("100.63.0.1")));
        Assert.False(NetworkEndpointSource.IsTailscaleAddress(IPAddress.Parse("100.128.0.1")));
        Assert.False(NetworkEndpointSource.IsTailscaleAddress(IPAddress.Parse("192.168.1.20")));
    }

    /// <summary>
    /// A carrier-grade NAT address on a real network card is not Tailscale.
    /// </summary>
    /// <remarks>
    /// 100.64.0.0/10 is the range carrier-grade NAT is built from, so a machine behind a mobile hotspot —
    /// or one of the ISPs that use it — has an ordinary 100.x address on its Wi-Fi card. Calling that
    /// Tailscale put a LAN address under "Phone on another network", labelled "reaches your phone from any
    /// network": the one thing it certainly cannot do.
    /// </remarks>
    [Fact]
    public void A_carrier_nat_address_on_a_physical_card_is_not_tailscale()
    {
        var wifi = new StubAdapter("Wi-Fi", "Intel Wireless-AC", NetworkInterfaceType.Wireless80211);

        Assert.Equal(PhoneEndpointKind.Lan,
            NetworkEndpointSource.Classify(wifi, IPAddress.Parse("100.100.0.5")));
    }

    [Fact]
    public void The_same_address_on_a_tunnel_is_tailscale()
    {
        var tunnel = new StubAdapter("nordlynx", "tunnel", NetworkInterfaceType.Tunnel);

        Assert.Equal(PhoneEndpointKind.Tailscale,
            NetworkEndpointSource.Classify(tunnel, IPAddress.Parse("100.100.0.5")));
    }

    [Fact]
    public void The_adapter_name_alone_is_enough()
    {
        var named = new StubAdapter("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet);

        Assert.Equal(PhoneEndpointKind.Tailscale,
            NetworkEndpointSource.Classify(named, IPAddress.Parse("10.1.2.3")));
    }

    [Fact]
    public void A_tunnel_without_a_tailscale_address_is_a_vpn()
    {
        var tunnel = new StubAdapter("wg0", "WireGuard", NetworkInterfaceType.Tunnel);

        Assert.Equal(PhoneEndpointKind.Vpn,
            NetworkEndpointSource.Classify(tunnel, IPAddress.Parse("10.7.0.2")));
    }

    /// <summary>Only the three things <c>Classify</c> reads; the rest of the surface is not needed.</summary>
    private sealed class StubAdapter(string name, string description, NetworkInterfaceType type)
        : NetworkInterface
    {
        public override string Id => name;
        public override string Name => name;
        public override string Description => description;
        public override NetworkInterfaceType NetworkInterfaceType => type;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override long Speed => 0;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;

        public override IPInterfaceProperties GetIPProperties() => throw new NotSupportedException();
        public override IPInterfaceStatistics GetIPStatistics() => throw new NotSupportedException();
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
        public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) => false;
    }
}
