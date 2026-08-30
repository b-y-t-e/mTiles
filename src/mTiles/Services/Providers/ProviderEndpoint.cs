using System.Net;

namespace mTiles.Services.Providers;

/// <summary>
/// What somebody types into an address field, as an address.
/// </summary>
/// <remarks>
/// <para>Pure and table-tested, in the style of <c>PhoneEndpointRanker</c>, because the whole of its
/// behaviour is a set of opinions about what a person meant: a bare host takes the provider's default
/// port, <c>host:port</c> is a host and a port and not a scheme, a full URL is taken as typed, and an
/// IPv6 literal has to be bracketed before either of those readings is even legal.</para>
/// <para>Called at every use rather than on the way in, so the stored text stays what the user typed.
/// </para>
/// </remarks>
public static class ProviderEndpoint
{
    /// <summary>
    /// The address <paramref name="text"/> names, or null when it names nothing usable.
    /// </summary>
    /// <param name="defaultPort">The port to fill in when the text does not carry one. A local server's
    /// port is the part people leave off, which is the only reason this parameter exists.</param>
    /// <remarks>Null for blank input rather than a default address: "nothing typed" means the provider's
    /// own address, and only the provider knows what that is. Null for unusable input too — the caller
    /// has one thing to say either way, which is that this field does not name a server.</remarks>
    public static Uri? Parse(string? text, int defaultPort)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        // A scheme was typed, so nothing here is being guessed at: it is a URL or it is not one.
        if (trimmed.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
                   && absolute.Scheme is "http" or "https"
                ? AsBase(absolute)
                : null;

        var (host, port) = SplitHostAndPort(trimmed);
        if (host is null)
            return null;

        // http, not https: what is typed without a scheme is a machine on this network, and a local
        // server serves plain HTTP. A hosted provider is reached through its own address, which carries
        // its scheme.
        return Uri.TryCreate($"http://{host}:{port ?? defaultPort}", UriKind.Absolute, out var built)
            ? built
            : null;
    }

    /// <summary>
    /// The same address, in the shape a base address has to be in.
    /// </summary>
    /// <remarks>Every caller composes this with <c>new Uri(base, relative)</c>, and RFC 3986 resolves a
    /// relative reference against a base by dropping everything after the base's last slash — so
    /// <c>https://gw.example.com/openai</c> plus <c>v1</c> is <c>https://gw.example.com/v1</c> and the
    /// gateway's own path is gone. A trailing slash is exactly what stops that, and it is the form a
    /// provider's own defaults are already written in. The field exists for proxies and gateways, and a
    /// path without the final slash is what their documentation prints.</remarks>
    private static Uri AsBase(Uri url) =>
        url.AbsolutePath.EndsWith('/')
            ? url
            : new UriBuilder(url) { Path = url.AbsolutePath + "/" }.Uri;

    /// <summary>
    /// The host and the port in <c>host</c>, <c>host:port</c>, <c>[::1]</c> or <c>[::1]:port</c>.
    /// </summary>
    /// <remarks>The bracketed forms are what make this more than a <c>Split(':')</c>: a bare
    /// <c>::1</c> is all colons and no port, so an unbracketed literal is bracketed here rather than
    /// read as a host called <c>:</c> with a port of <c>:1</c>.</remarks>
    private static (string? Host, int? Port) SplitHostAndPort(string text)
    {
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
                return (null, null);

            var literal = text[..(close + 1)];
            var rest = text[(close + 1)..];
            if (rest.Length == 0)
                return (literal, null);

            return rest.StartsWith(':') && TryPort(rest[1..], out var bracketed)
                ? (literal, bracketed)
                : (null, null);
        }

        // Unbracketed, and more than one colon: an IPv6 literal somebody typed without its brackets.
        // Reading it as host and port would produce a host of "fe80" and lose the address.
        if (text.Count(c => c == ':') > 1)
            return IPAddress.TryParse(text, out var address) ? ($"[{address}]", null) : (null, null);

        var separator = text.IndexOf(':');
        if (separator < 0)
            return (text, null);

        var host = text[..separator];
        return host.Length > 0 && TryPort(text[(separator + 1)..], out var port)
            ? (host, port)
            : (null, null);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, out port) && port is > 0 and <= 65535;
}
