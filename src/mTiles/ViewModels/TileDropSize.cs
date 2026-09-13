namespace mTiles.ViewModels;

/// <summary>
/// How much room a tile is given when it is put beside another: a size in pixels, or a share of the room.
/// </summary>
/// <remarks>
/// <para>Two answers, not one, because they behave differently afterwards. Pixels stay what they are when
/// the window is resized — the list of workspaces, a note kept beside the layout — while a share grows and
/// shrinks with it, which is what every tile inside a workspace does. Which one a drop gets is decided by
/// whoever asks (<c>WindowTileSize</c> for the window's own tiles); this only carries the answer to the
/// edit that splits the room and to the hint that draws it.</para>
/// <para>No value at all is the third answer, and it is the one a workspace gives: the share each edit has
/// always used — half beside a tile, a third beside everything or between two.</para>
/// </remarks>
internal readonly record struct TileDropSize
{
    private TileDropSize(double? pixels, double? share)
    {
        Pixels = pixels;
        Share = share;
    }

    /// <summary>The newcomer's size in pixels, or null when it is given a share.</summary>
    public double? Pixels { get; }

    /// <summary>The newcomer's share of the room it is put into, or null when it is given pixels.</summary>
    public double? Share { get; }

    public static TileDropSize InPixels(double pixels) => new(pixels, null);

    public static TileDropSize AsShare(double share) => new(null, share);

    /// <summary>Pixels that can be laid out, or null.</summary>
    public double? UsablePixels => Pixels is { } p && SplitTileNodeViewModel.IsUsableExtent(p) ? p : null;

    /// <summary>A share strictly between nothing and everything, or null.</summary>
    public double? UsableShare => Share is { } s && s > 0 && s < 1 ? s : null;

    /// <summary>Whether this carries a size an edit can apply — usable pixels or a usable share.</summary>
    /// <remarks>The one test the edit and the hint both ask, so they cannot disagree about whether a size
    /// was given and fall back to different defaults.</remarks>
    public bool IsUsable => UsablePixels is not null || UsableShare is not null;
}
