namespace mTiles.Services.Browser;

/// <summary>Reaches every browser tile in the window at once.</summary>
public static class BrowserTiles
{
    /// <summary>Closes them all, answering whether there was anything to close.</summary>
    public static Func<bool>? CloseAllHandler { get; set; }

    public static bool CloseAll() => CloseAllHandler?.Invoke() ?? false;
}
