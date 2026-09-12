using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The tile drag in flight, if there is one: which tile is being dragged, and who is showing a hint for
/// it.
/// </summary>
/// <remarks>
/// <para>Static because a drag is: the platform runs one at a time for the whole application, and the
/// surface a pointer is over has no other way to find out what is being carried — the payload handed to
/// the platform is only a marker, since a view model is not something a clipboard format can hold.</para>
/// <para><b>Which tree a drag belongs to is read, never stored.</b> <see cref="IsFrom"/> walks the
/// dragged tile up to its root and compares, which is what lets the window's surface and a workspace's
/// surface be nested one inside the other and each answer only for its own tiles — with nothing to keep
/// in step when a tile moves, because a tile that has moved already has a different root.</para>
/// </remarks>
internal static class TileDragSession
{
    /// <summary>What the platform's drag carries, so a drop from another application is told apart.</summary>
    public const string DataFormat = "application/x-mtiles-tile";

    /// <summary>The tile being dragged, or null when no tile drag is in flight.</summary>
    public static LeafTileNodeViewModel? Source { get; private set; }

    private static Action? _clearHint;

    /// <summary>Starts a drag of <paramref name="source"/>.</summary>
    public static void Begin(LeafTileNodeViewModel source)
    {
        End();
        Source = source;
    }

    /// <summary>Ends the drag, however it ended, and puts away whatever hint is still on screen.</summary>
    /// <remarks>Called from the drag handle's <c>finally</c>, the one place that always runs. A drag
    /// abandoned with Escape over a tile is not guaranteed to raise <c>DragLeave</c> anywhere, and a band
    /// of accent left painted across a layout for the rest of the session is the kind of thing a user has
    /// to restart the application to be rid of.</remarks>
    public static void End()
    {
        var clear = _clearHint;
        _clearHint = null;
        Source = null;
        clear?.Invoke();
    }

    /// <summary>Whether the tile being dragged hangs in the tree rooted at <paramref name="root"/>.</summary>
    public static bool IsFrom(TileNodeViewModel? root) =>
        Source is { } source && root is not null && ReferenceEquals(TileTreeEdits.RootOf(source), root);

    /// <summary>Records who is showing a hint now, so that the previous one is put away.</summary>
    /// <remarks>Two surfaces are on screen at once as soon as there are two levels, and a pointer moving
    /// from one to the other must not leave the first one's band behind: the second surface cannot reach
    /// the first, but the session reaches both.</remarks>
    public static void Hinting(Action clear)
    {
        var previous = _clearHint;
        _clearHint = clear;
        if (previous is not null && previous != clear) previous();
    }
}
