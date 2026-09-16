namespace mTiles.Services.Browser;

/// <summary>The proxy setting, checked and turned into what the engine is given.</summary>
/// <remarks>
/// <para>Checked because it ends up on the browser process's command line: a value carrying a space or
/// a quote would be a second argument somebody typed into a settings field, so anything that is not a
/// scheme, a host and a port is refused rather than passed through.</para>
/// <para>A refusal is a sentence rather than an exception — the settings page shows it under the field,
/// and the tile goes direct rather than failing to open.</para>
/// </remarks>
public static class BrowserProxy
{
    private static readonly string[] Schemes = ["http", "https", "socks4", "socks5"];

    /// <summary>The value normalised to <c>scheme://host:port</c>, or null for "no proxy".</summary>
    /// <param name="problem">Why a non-empty value was refused; null when it was not.</param>
    public static string? Normalise(string? setting, out string? problem)
    {
        problem = null;
        var text = setting?.Trim() ?? "";
        if (text.Length == 0)
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !Schemes.Contains(uri.Scheme)
            || uri.Host.Length == 0
            || uri.IsDefaultPort && !HasExplicitPort(text)
            || uri.AbsolutePath is not ("/" or "")
            || uri.Query.Length > 0
            || uri.UserInfo.Length > 0)
        {
            problem = "Expected socks5://host:port or http://host:port.";
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>The proxy switch the WebView2 browser process is started with, or null for none.</summary>
    public static string? ProxyArgument(string? setting) =>
        // No bypass list: Chromium already leaves loopback out of the proxy, so a page served by this
        // machine stays reachable while the rest goes out through the relay.
        Normalise(setting, out _) is { } proxy ? $"--proxy-server={proxy}" : null;

    /// <summary>Everything the browser process is started with for this workspace's settings, or null.</summary>
    /// <remarks>Proxy and secure DNS are one string because WebView2 runs one browser process and refuses a
    /// view whose arguments differ from it — so both have to be decided together, when the first tile
    /// opens.</remarks>
    public static string? BrowserArguments(mTiles.Models.BrowserSettings browser)
    {
        var parts = new List<string>();
        if (ProxyArgument(browser.ProxyServer) is { } proxy)
            parts.Add(proxy);
        if (SecureDnsResolver.HostRulesArgument(browser) is { } dns)
            parts.Add(dns);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static bool HasExplicitPort(string text)
    {
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var colon = authority.LastIndexOf(':');
        return colon > authority.LastIndexOf(']') && colon < authority.Length - 1;
    }
}
