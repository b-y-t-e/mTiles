using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The tile drag in flight, if there is one: which tile is being dragged, and who is showing a hint for
/// it.
/// </summary>
/// <remarks>
/// <para>Static because a drag is: there is one pointer dragging at a time for the whole application, and
/// the surface under it has no other way to find out what is being carried.</para>
/// <para><b>Which tree a drag belongs to is read, never stored.</b> <see cref="IsFrom"/> walks the
/// dragged tile up to its root and compares, which is what lets the window's surface and a workspace's
/// surface be nested one inside the other and each answer only for its own tiles — with nothing to keep
/// in step when a tile moves, because a tile that has moved already has a different root.</para>
/// </remarks>
internal static class TileDragSession
{
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
        Source = null;
        ClearHint();
    }

    /// <summary>The pointer is at <paramref name="point"/> in <paramref name="window"/>: the innermost
    /// surface under it that answers shows its hint, and with none answering every hint is put away.</summary>
    /// <remarks>Innermost first and outward, which is the bubbling the platform's drag events did: a
    /// workspace's surface leaves a window-level tile to the window's surface around it.</remarks>
    public static void Over(TopLevel window, Point point) => Over(HitIn(window, point), window, point);

    /// <summary>The drag was released at <paramref name="point"/> in <paramref name="window"/>.</summary>
    public static void Drop(TopLevel window, Point point) => Drop(HitIn(window, point), window, point);

    /// <summary><see cref="Over(TopLevel, Point)"/> once the window has named what is under the pointer.</summary>
    internal static void Over(Visual? hit, Visual relativeTo, Point point)
    {
        foreach (var surface in SurfacesAbove(hit))
            if (surface.DragOver(relativeTo, point)) return;

        ClearHint();
    }

    /// <summary><see cref="Drop(TopLevel, Point)"/> once the window has named what is under the pointer.</summary>
    internal static void Drop(Visual? hit, Visual relativeTo, Point point)
    {
        foreach (var surface in SurfacesAbove(hit))
            if (surface.Drop(relativeTo, point)) break;

        ClearHint();
    }

    private static Visual? HitIn(TopLevel window, Point point) => window.InputHitTest(point) as Visual;

    private static IEnumerable<ITileDragSurface> SurfacesAbove(Visual? hit) =>
        hit?.GetSelfAndVisualAncestors().OfType<ITileDragSurface>() ?? [];

    private static void ClearHint()
    {
        var clear = _clearHint;
        _clearHint = null;
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
