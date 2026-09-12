using Avalonia;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>What the pointer is over during a tile drag, in the order the three are asked.</summary>
internal enum TileDropKind
{
    /// <summary>Nothing this gesture can be dropped on.</summary>
    None,

    /// <summary>The outer band of the tree — a new column or row beside the whole layout.</summary>
    RootEdge,

    /// <summary>The gutter of a split — between the two tiles it holds.</summary>
    Gutter,

    /// <summary>A tile: its middle swaps, its edges split it.</summary>
    Leaf
}

/// <summary>
/// Where on screen a dragged tile would land: the zones of a tile, the band along the tree's edge, and
/// the room a split will have once the dragged tile has left it.
/// </summary>
/// <remarks>Pure, and separate from both the tree edits and the surface that draws the hints, so each
/// rule is readable in a table test without a window.</remarks>
internal static class TileDropGeometry
{
    /// <summary>How far into the tree the outer drop band reaches.</summary>
    /// <remarks>
    /// <para>It has to overlap the outermost tiles, and that is forced rather than chosen: the
    /// workspace's padding is eight pixels on three sides and <b>nothing on the left</b>, where the gap
    /// is the panel's own splitter column and belongs to the window. A band living only in the padding
    /// would therefore have no left edge at all.</para>
    /// <para>So it wins over the tile underneath, and the width is the price of that: wide enough to
    /// hit with a mouse, narrow enough that a tile 200px across keeps most of its own 30% edge zone.
    /// Capped at a third of the shorter side so that a workspace narrower than two bands still has a
    /// middle.</para>
    /// </remarks>
    public const double RootEdgeBand = 28;

    public static DropZone GetDropZone(Point position, Size bounds)
    {
        if (bounds.Width < 40 || bounds.Height < 40)
            return DropZone.Center;

        var rx = position.X / bounds.Width;
        var ry = position.Y / bounds.Height;

        const double edge = 0.30;

        var dLeft = rx;
        var dRight = 1 - rx;
        var dTop = ry;
        var dBottom = 1 - ry;
        var minD = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        if (minD >= edge)
            return DropZone.Center;

        if (minD == dLeft) return DropZone.Left;
        if (minD == dRight) return DropZone.Right;
        if (minD == dTop) return DropZone.Top;
        return DropZone.Bottom;
    }

    /// <summary>
    /// Which edge of the tree the pointer is in the band of, or <see cref="DropZone.None"/>.
    /// </summary>
    /// <remarks>A position <em>outside</em> the bounds answers with the nearest side rather than with
    /// nothing: a surface's padding is drawn outside the tile tree, and a pointer in it is as much
    /// on that edge as one a pixel inside is.</remarks>
    public static DropZone GetRootEdge(Point position, Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return DropZone.None;

        var band = Math.Min(RootEdgeBand, Math.Min(bounds.Width, bounds.Height) / 3);

        var dLeft = position.X;
        var dRight = bounds.Width - position.X;
        var dTop = position.Y;
        var dBottom = bounds.Height - position.Y;
        var minD = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        if (minD > band) return DropZone.None;

        if (minD == dLeft) return DropZone.Left;
        if (minD == dRight) return DropZone.Right;
        if (minD == dTop) return DropZone.Top;
        return DropZone.Bottom;
    }

    /// <summary>
    /// Where a rectangle inside the lifted sibling ends up once that sibling fills the slot it shared.
    /// </summary>
    /// <param name="rect">What is being moved, in the same coordinates as the other two.</param>
    /// <param name="lifted">The sibling's bounds now.</param>
    /// <param name="vacated">The bounds of the split that held the sibling and the dragged tile.</param>
    public static Rect AfterDetach(Rect rect, Rect lifted, Rect vacated)
    {
        if (lifted.Width <= 0 || lifted.Height <= 0) return rect;

        var scaleX = vacated.Width / lifted.Width;
        var scaleY = vacated.Height / lifted.Height;

        return new Rect(
            vacated.X + (rect.X - lifted.X) * scaleX,
            vacated.Y + (rect.Y - lifted.Y) * scaleY,
            rect.Width * scaleX,
            rect.Height * scaleY);
    }
}
