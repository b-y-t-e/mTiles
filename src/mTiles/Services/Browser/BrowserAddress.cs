namespace mTiles.Services.Browser;

/// <summary>What a line typed into the browser tile's address box means.</summary>
/// <remarks>
/// Pure, and argued in a table test, because it is an opinion: <c>example.com</c> is an address,
/// <c>two words</c> is a search, and <c>localhost:5000</c> is an address although it has no dot in it.
/// Nothing typed is ever refused — a line that is not an address is a search, which is what every
/// browser's box does and therefore what a hand reaching for it expects.
/// </remarks>
public static class BrowserAddress
{
    /// <summary>Where a line that is not an address is sent.</summary>
    public const string SearchPrefix = "https://www.google.com/search?q=";

    /// <summary>The page a typed line opens, or null for a blank one.</summary>
    public static Uri? Resolve(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0)
            return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https" or "about" or "file")
            return absolute;

        if (!text.Contains(' ') && LooksLikeHost(text)
            && Uri.TryCreate((IsLocal(text) ? "http://" : "https://") + text, UriKind.Absolute, out var host))
            return host;

        return new Uri(SearchPrefix + Uri.EscapeDataString(text));
    }

    /// <summary>The home page setting, or a blank page when it says nothing usable.</summary>
    public static Uri Home(string? setting) => Resolve(setting) ?? new Uri("about:blank");

    private static bool LooksLikeHost(string text)
    {
        var host = text.Split('/', '?', '#')[0];
        if (host.Length == 0)
            return false;

        var name = host.Split(':')[0];
        return IsLocal(text) || (name.Contains('.') && !name.StartsWith('.') && !name.EndsWith('.'));
    }

    private static bool IsLocal(string text) =>
        text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("127.", StringComparison.Ordinal);
}
