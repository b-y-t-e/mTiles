using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// How small a tile subtree may be squeezed to before one of the tiles inside it disappears.
/// </summary>
/// <remarks>
/// <para>A minimum on the pane being dragged is not enough, because a pane is not a tile: a star-sized
/// column takes the size the splitter gives it and never grows to what its content needs, so a column
/// holding a further split at 50 px lays its own two tiles out beyond the column's edge, under the
/// opaque card next door — the tile is gone, which is the very thing the minimum exists to prevent.
/// The minimum is therefore a property of the whole subtree: every leaf along the axis wants
/// <see cref="LeafSize"/>, and every split along it also spends a gutter.</para>
/// <para>Pure, and told the gutter rather than knowing it, so the rule can be read in a test without a
/// window and so this does not depend on a view's constant.</para>
/// </remarks>
public static class TileMinimumSize
{
    /// <summary>The smallest a single tile may be made along either axis, in pixels.</summary>
    /// <remarks>Enough to keep the tile's header — the handle it would be dragged back out by — on
    /// screen, so no drag is one the user cannot undo.</remarks>
    public const double LeafSize = 50;

    /// <summary>The narrowest <paramref name="node"/> can be with every tile in it still visible.</summary>
    public static double Width(TileNodeViewModel? node, double gap) => Along(node, Orientation.Vertical, gap);

    /// <summary>The shortest <paramref name="node"/> can be with every tile in it still visible.</summary>
    public static double Height(TileNodeViewModel? node, double gap) => Along(node, Orientation.Horizontal, gap);

    /// <summary>
    /// The same two minimums, brought back inside <paramref name="available"/> when they do not fit.
    /// </summary>
    /// <remarks>
    /// <para>A minimum is a floor the layout will not go below, so two of them that add up to more than
    /// there is do not shrink anything — they push the far pane out past the edge, where it is clipped.
    /// That is the disappearing tile again, arrived at by narrowing the window instead of by dragging
    /// the splitter, and it is worse than no minimum: without one the panes at least shared what space
    /// there was.</para>
    /// <para>So below the width where every tile can keep its 50 px, they go back to sharing
    /// proportionally. The guarantee is the one the goal asks for — a splitter cannot squeeze a tile
    /// away — not a promise about a window too small to hold the tiles at all.</para>
    /// <para><paramref name="available"/> of zero or less means the size is not known yet (nothing has
    /// been laid out), which is not the same as no room: the full minimums stand until it is.</para>
    /// </remarks>
    public static (double First, double Second) Fit(double first, double second, double available)
    {
        var wanted = first + second;
        if (available <= 0 || wanted <= available) return (first, second);

        var scale = available / wanted;
        return (first * scale, second * scale);
    }

    /// <param name="dividingAxis">The split orientation that divides the axis being measured — a
    /// <see cref="Orientation.Vertical"/> split puts its children side by side and so divides width.</param>
    private static double Along(TileNodeViewModel? node, Orientation dividingAxis, double gap)
    {
        if (node is not SplitTileNodeViewModel split)
            return LeafSize;

        var first = Along(split.First, dividingAxis, gap);
        var second = Along(split.Second, dividingAxis, gap);
        return split.Orientation == dividingAxis ? first + gap + second : Math.Max(first, second);
    }
}
