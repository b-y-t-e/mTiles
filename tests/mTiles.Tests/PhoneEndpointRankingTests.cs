using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which address the QR code points at.
/// </summary>
/// <remarks>
/// The only part of the phone bridge whose behaviour is a judgement rather than a fact, and the part a
/// user notices when it is wrong: the code they scan either loads a page or does nothing at all. Tested
/// here without a network card, a phone or a server, which is the whole reason
/// <see cref="PhoneEndpointRanker"/> is pure.
/// </remarks>
public class PhoneEndpointRankingTests
{
    private static PhoneEndpoint Lan(string host, bool gateway = true, string name = "Wi-Fi") =>
        new(host, PhoneEndpointKind.Lan, name, name, gateway, false);

    private static PhoneEndpoint Tailscale(string host = "pc.tail1234.ts.net") =>
        new(host, PhoneEndpointKind.Tailscale, "Tailscale", "Tailscale", true, true);

    private static PhoneEndpoint Virtual(string host, string name) =>
        new(host, PhoneEndpointKind.Lan, name, name, false, false);

    [Fact]
    public void At_the_console_the_lan_address_is_recommended_first()
    {
        var board = PhoneEndpointRanker.Rank(
            [Tailscale(), Lan("192.168.1.20")], SessionLocation.Console, pinnedHost: null);

        Assert.Equal(PhoneEndpointAudience.SameNetwork, board.Preferred);
        Assert.Equal("192.168.1.20", board.Recommended[0].Endpoint.Host);
    }

    [Fact]
    public void Over_remote_desktop_the_tunnel_is_recommended_first()
    {
        var board = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.20"), Tailscale()], SessionLocation.Remote, pinnedHost: null);

        Assert.Equal(PhoneEndpointAudience.Remote, board.Preferred);
        Assert.Equal("pc.tail1234.ts.net", board.Recommended[0].Endpoint.Host);
    }

    /// <summary>
    /// The failure the two-audience design exists to prevent: being wrong about the order must never
    /// mean the other option is off the screen.
    /// </summary>
    [Fact]
    public void Both_audiences_are_offered_whichever_session_it_is()
    {
        foreach (var location in new[] { SessionLocation.Console, SessionLocation.Remote, SessionLocation.Unknown })
        {
            var board = PhoneEndpointRanker.Rank(
                [Lan("192.168.1.20"), Tailscale()], location, pinnedHost: null);

            Assert.NotNull(board.SameNetwork);
            Assert.NotNull(board.Remote);
            Assert.Equal(2, board.Recommended.Count);
        }
    }

    /// <summary>
    /// A developer machine carries Hyper-V, WSL and Docker adapters whose addresses are RFC1918 and look
    /// exactly like a home network. The default route is what tells them apart.
    /// </summary>
    [Fact]
    public void An_adapter_with_no_default_route_never_outranks_a_real_one()
    {
        var board = PhoneEndpointRanker.Rank(
            [Virtual("172.28.112.1", "vEthernet (WSL)"), Virtual("192.168.56.1", "VirtualBox Host-Only"),
             Lan("192.168.1.20")],
            SessionLocation.Console, pinnedHost: null);

        Assert.Equal("192.168.1.20", board.SameNetwork!.Endpoint.Host);
        Assert.Equal("192.168.1.20", board.All[0].Endpoint.Host);
    }

    /// <summary>What a phone actually reached beats every heuristic here.</summary>
    [Fact]
    public void The_address_that_worked_last_time_wins_its_audience()
    {
        var board = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.20"), Lan("10.0.0.5", name: "Ethernet")],
            SessionLocation.Console, pinnedHost: "10.0.0.5");

        Assert.Equal("10.0.0.5", board.SameNetwork!.Endpoint.Host);
        Assert.StartsWith("Worked last time", board.SameNetwork.Reason);
    }

    /// <summary>
    /// The bonus adjusts an order; it does not invert a class. A remembered LAN address is still useless
    /// to a phone that is not on that LAN, so it must not displace the tunnel as the remote answer.
    /// </summary>
    [Fact]
    public void A_remembered_lan_address_does_not_become_the_remote_recommendation()
    {
        var board = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.20"), Tailscale()], SessionLocation.Remote, pinnedHost: "192.168.1.20");

        Assert.Equal("pc.tail1234.ts.net", board.Remote!.Endpoint.Host);
        Assert.Equal("192.168.1.20", board.SameNetwork!.Endpoint.Host);
        Assert.Equal("pc.tail1234.ts.net", board.Recommended[0].Endpoint.Host);
    }

    /// <summary>A machine with no tunnel offers one code, not a broken second one.</summary>
    [Fact]
    public void With_no_tunnel_there_is_no_remote_recommendation()
    {
        var board = PhoneEndpointRanker.Rank([Lan("192.168.1.20")], SessionLocation.Remote, null);

        Assert.Null(board.Remote);
        Assert.Single(board.Recommended);
    }

    [Fact]
    public void Nothing_discovered_is_an_empty_board_rather_than_a_throw()
    {
        var board = PhoneEndpointRanker.Rank([], SessionLocation.Unknown, null);

        Assert.Empty(board.All);
        Assert.Empty(board.Recommended);
        Assert.Null(board.SameNetwork);
    }

    /// <summary>
    /// A QR code that moves while somebody is photographing it is its own bug, so equally-scored
    /// addresses must come back in a stable order rather than whatever the adapter enumeration felt like.
    /// </summary>
    [Fact]
    public void Equal_candidates_keep_a_stable_order()
    {
        var first = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.30"), Lan("192.168.1.20")], SessionLocation.Console, null);
        var again = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.20"), Lan("192.168.1.30")], SessionLocation.Console, null);

        Assert.Equal(
            first.All.Select(e => e.Endpoint.Host),
            again.All.Select(e => e.Endpoint.Host));
    }

    /// <summary>The same address reported by two sources is one row, not two identical ones.</summary>
    [Fact]
    public void Duplicate_hosts_collapse()
    {
        var board = PhoneEndpointRanker.Rank(
            [Lan("192.168.1.20"), Lan("192.168.1.20", name: "Wi-Fi 2")], SessionLocation.Console, null);

        Assert.Single(board.All);
    }
}
