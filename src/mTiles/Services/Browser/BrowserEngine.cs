using mTiles.Models;

namespace mTiles.Services.Browser;

/// <summary>How the one browser process every browser tile shares is started.</summary>
/// <remarks>
/// <para><b>One process, one set of arguments.</b> WebView2 runs one browser process per profile
/// directory, and it refuses to create a view whose arguments differ from those the running process was
/// started with. So the arguments are fixed the moment the first tile opens and held while any tile is
/// open; a proxy changed meanwhile applies once the last one has closed. Asking for the settings' value
/// every time instead is a tile that fails to open, with an error naming neither the proxy nor the fix.
/// </para>
/// <para><b>Its own profile directory, under this application's.</b> WebView2's default is a folder
/// beside the executable, which Velopack replaces on every update — a sign-in to any site would last until
/// the next release.</para>
/// <para>Passed as environment <em>options</em>, never as the <c>WEBVIEW2_*</c> environment variables:
/// those would be inherited by every shell a terminal tile starts, and by whatever WebView2 application
/// is run from one.</para>
/// </remarks>
public static class BrowserEngine
{
    private static int _open;
    private static string? _arguments;

    /// <summary>Where the shared profile lives.</summary>
    public static string ProfileDirectory => Path.Combine(AppPaths.GetAppDataDirectory(), "browser");

    /// <summary>The arguments a view opening now is created with, taking a hold on them.</summary>
    /// <remarks>Called on the UI thread, as every view is created there; the matching
    /// <see cref="Release"/> is the view closing.</remarks>
    public static string? Acquire(BrowserSettings settings)
    {
        if (_open++ == 0)
            _arguments = OperatingSystem.IsWindows() ? BrowserProxy.BrowserArguments(settings) : null;
        return _arguments;
    }

    public static void Release()
    {
        if (_open > 0)
            _open--;
    }

    /// <summary>Whether the browser process is running on arguments other than the settings' own.</summary>
    /// <remarks>What the tile says under its address box: a proxy typed into Settings while a page
    /// is open does nothing yet, and silence would read as the proxy not working.</remarks>
    public static bool IsBehind(BrowserSettings settings) =>
        _open > 0 && OperatingSystem.IsWindows()
        && _arguments != BrowserProxy.BrowserArguments(settings);
}
