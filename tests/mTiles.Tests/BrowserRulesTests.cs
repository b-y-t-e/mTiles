using System.Net;
using System.Text;
using mTiles.Services.Browser;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a typed address means, what a proxy setting may put on a command line, and who the relay
/// answers and where it may connect.
/// </summary>
/// <remarks>The last two are security rules that cannot be watched working from the outside, which is
/// why each is a row here rather than a sentence in a comment.</remarks>
public class BrowserRulesTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com/watch?v=1  ", "https://example.com/watch?v=1")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("localhost:5000", "http://localhost:5000/")]
    [InlineData("127.0.0.1:8080/x", "http://127.0.0.1:8080/x")]
    [InlineData("about:blank", "about:blank")]
    public void An_address_opens_as_itself(string typed, string expected) =>
        Assert.Equal(expected, BrowserAddress.Resolve(typed)!.ToString());

    [Theory]
    [InlineData("two words")]
    [InlineData("word")]
    [InlineData("example.com is down")]
    [InlineData(".hidden")]
    [InlineData("javascript:alert(1)")]
    public void Anything_else_is_a_search(string typed) =>
        Assert.StartsWith(BrowserAddress.SearchPrefix, BrowserAddress.Resolve(typed)!.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_opens_nothing(string? typed) => Assert.Null(BrowserAddress.Resolve(typed));

    [Fact]
    public void An_unusable_home_page_is_a_blank_page() =>
        Assert.Equal("about:blank", BrowserAddress.Home("").ToString());

    [Theory]
    [InlineData("socks5://100.64.0.1:1080", "socks5://100.64.0.1:1080")]
    [InlineData("http://100.64.0.1:18093", "http://100.64.0.1:18093")]
    [InlineData("100.64.0.1:18093", "http://100.64.0.1:18093")]
    [InlineData(" http://proxy.example:80/ ", "http://proxy.example:80")]
    [InlineData("http://[fd7a:115c:a1e0::1]:18093", "http://[fd7a:115c:a1e0::1]:18093")]
    public void A_proxy_is_normalised(string setting, string expected)
    {
        Assert.Equal(expected, BrowserProxy.Normalise(setting, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("http://host")]
    [InlineData("ftp://host:21")]
    [InlineData("http://host:80/path")]
    [InlineData("http://user:pw@host:80")]
    [InlineData("http://host:80 --disable-web-security")]
    [InlineData("http://host:80\" --x")]
    [InlineData("http://host:80?x=1")]
    public void A_proxy_that_is_not_just_a_host_and_port_is_refused(string setting)
    {
        Assert.Null(BrowserProxy.Normalise(setting, out var problem));
        Assert.NotNull(problem);
        Assert.Null(BrowserProxy.ProxyArgument(setting));
    }

    [Fact]
    public void No_proxy_is_no_argument_and_no_problem()
    {
        Assert.Null(BrowserProxy.Normalise("  ", out var problem));
        Assert.Null(problem);
        Assert.Null(BrowserProxy.ProxyArgument(""));
    }

    [Fact]
    public void The_host_rule_is_quoted_because_each_map_carries_spaces()
    {
        var rule = SecureDnsResolver.BuildRule([("www.youtube.com", "1.2.3.4"), ("youtube.com", "5.6.7.8")]);

        // Unquoted, WebView2 cut the value at the first space — measured, and the whole reason for it.
        Assert.Equal("--host-resolver-rules=\"MAP www.youtube.com 1.2.3.4,MAP youtube.com 5.6.7.8\"", rule);
    }

    [Fact]
    public void Nothing_resolved_is_no_rule()
    {
        Assert.Null(SecureDnsResolver.BuildRule([]));

        // Off, or an unusable resolver, means no lookup and no switch.
        Assert.Null(SecureDnsResolver.HostRulesArgument(new mTiles.Models.BrowserSettings { SecureDns = false }));
        Assert.Null(SecureDnsResolver.HostRulesArgument(
            new mTiles.Models.BrowserSettings { SecureDns = true, SecureDnsTemplate = "http://insecure/dns" }));
    }

    [Fact]
    public void The_video_hosts_are_not_mapped()
    {
        // Mapping a googlevideo name to one address would break playback — it is many hosts a session —
        // and it is not the one a filter tampers with, so it must stay out of the list.
        Assert.DoesNotContain(SecureDnsResolver.Hosts, host => host.Contains("googlevideo"));
        Assert.Contains("www.youtube.com", SecureDnsResolver.Hosts);
    }

    [Fact]
    public void A_proxy_becomes_one_argument() =>
        Assert.Equal("--proxy-server=socks5://100.64.0.1:1080",
            BrowserProxy.ProxyArgument("socks5://100.64.0.1:1080"));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("::ffff:100.100.1.1", true)]
    [InlineData("fd7a:115c:a1e0::5", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Only_the_tailnet_is_answered(string address, bool answered) =>
        Assert.Equal(answered, RelayRules.IsTailnet(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("142.250.186.206", true)]
    [InlineData("2a00:1450:4001:80b::200e", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.100.100.100", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    public void Only_the_public_internet_is_reached(string address, bool reached) =>
        Assert.Equal(reached, RelayRules.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void A_connect_is_a_tunnel()
    {
        var request = Parse("CONNECT www.example.com:443 HTTP/1.1\r\nHost: www.example.com:443\r\n\r\n");

        Assert.NotNull(request);
        Assert.True(request.IsTunnel);
        Assert.Equal("www.example.com", request.Host);
        Assert.Equal(443, request.Port);
        Assert.Empty(request.Forward);
    }

    [Fact]
    public void A_connect_to_an_ipv6_literal_is_unbracketed()
    {
        var request = Parse("CONNECT [2a00:1450::1]:443 HTTP/1.1\r\n\r\n");

        Assert.Equal("2a00:1450::1", request!.Host);
        Assert.Equal(443, request.Port);
    }

    [Theory]
    [InlineData("CONNECT www.example.com HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT www.example.com:0 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT www.example.com:99999 HTTP/1.1\r\n\r\n")]
    [InlineData("GET /relative HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET https://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com/ SPDY/3\r\n\r\n")]
    [InlineData("garbage\r\n\r\n")]
    public void Anything_else_is_refused(string head) => Assert.Null(Parse(head));

    [Fact]
    public void A_plain_request_is_rewritten_for_the_origin()
    {
        var request = Parse(
            "GET http://example.com:8080/a?b=1 HTTP/1.1\r\n" +
            "Host: example.com:8080\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Proxy-Authorization: Basic eA==\r\n" +
            "Connection: keep-alive\r\n" +
            "Accept: */*\r\n\r\n");

        Assert.NotNull(request);
        Assert.False(request.IsTunnel);
        Assert.Equal("example.com", request.Host);
        Assert.Equal(8080, request.Port);
        Assert.Equal(
            "GET /a?b=1 HTTP/1.1\r\nHost: example.com:8080\r\nAccept: */*\r\nConnection: close\r\n\r\n",
            Encoding.Latin1.GetString(request.Forward));
    }

    [Fact]
    public void The_head_ends_at_the_blank_line()
    {
        Assert.Equal(-1, RelayRules.HeadLength("GET / HTTP/1.1\r\nHost: x\r\n"u8));
        Assert.Equal(27, RelayRules.HeadLength("GET / HTTP/1.1\r\nHost: x\r\n\r\nbody"u8));
    }

    private static RelayRequest? Parse(string head) => RelayRules.Parse(Encoding.Latin1.GetBytes(head));
}
