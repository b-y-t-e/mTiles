using Avalonia;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
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

    /// <summary>The room a tile dropped on an edge of <paramref name="area"/> will take, in its own coordinates.</summary>
    /// <remarks>Fixed pixels are drawn at the size the layout will actually give them: the cap
    /// <c>TileNodeView</c> puts on a fixed pane (<see cref="TileMinimumSize.FixedMaximum"/>), with the
    /// newcomer's own minimum on one side and <paramref name="besideMinimum"/> on the other. Without it a
    /// size wider than the room leaves draws a band the pane beside it is not giving up.</remarks>
    /// <param name="fixedExtent">The pixels the tile will be held at, or null for a third of the room.</param>
    /// <param name="gap">The gutter the new split puts between the tile and what it lands beside.</param>
    /// <param name="besideMinimum">The minimum, along the drop's axis, of what the tile lands beside.</param>
    public static Rect EdgeBand(Size area, DropZone zone, double? fixedExtent, double gap, double besideMinimum)
    {
        var horizontal = zone is DropZone.Left or DropZone.Right;
        var along = horizontal ? area.Width : area.Height;
        var sourceFirst = zone is DropZone.Left or DropZone.Top;

        var (start, size) = fixedExtent is { } extent && SplitTileNodeViewModel.IsUsableExtent(extent)
            ? AtEdge(along, LaidOutEdgeExtent(extent, along, gap, besideMinimum), sourceFirst)
            : Scale(TileDropRatio.EdgeBand(sourceFirst), along);

        return horizontal
            ? new Rect(start, 0, size, area.Height)
            : new Rect(0, start, area.Width, size);
    }

    /// <summary>The pixels a tile dropped on an edge will be laid out at, once the cap has had its say.</summary>
    private static double LaidOutEdgeExtent(double extent, double along, double gap, double besideMinimum)
    {
        var available = along - gap;
        var (fixedMinimum, flexibleMinimum) = TileMinimumSize.Fit(TileMinimumSize.LeafSize, besideMinimum, available);
        return Math.Min(extent, TileMinimumSize.FixedMaximum(fixedMinimum, flexibleMinimum, available));
    }

    /// <summary>A band <paramref name="extent"/> long against the start or the end of the room.</summary>
    private static (double Start, double Size) AtEdge(double along, double extent, bool atStart)
    {
        var size = Math.Clamp(extent, 0, Math.Max(0, along));
        return (atStart ? 0 : along - size, size);
    }

    /// <summary>
    /// The room a tile dropped on <paramref name="split"/>'s gutter will take, inside
    /// <paramref name="room"/> — the split's bounds once the dragged tile has left the tree.
    /// </summary>
    /// <remarks>The same rule <c>TileTreeEdits.ExecuteGutter</c> runs on, drawn rather than guessed: a
    /// split with a fixed side gives the newcomer a third of its other side only, beside the fixed tile's
    /// own pixels and the gutter between them, so a band spanning the whole split would promise room the
    /// fixed tile is not giving up.</remarks>
    /// <param name="gap">The gutter between the split's two panes.</param>
    public static Rect GutterBand(Rect room, SplitTileNodeViewModel split, double gap)
    {
        var vertical = split.Orientation == Orientation.Vertical;
        var along = vertical ? room.Width : room.Height;

        var (start, size) = split.FixedSide switch
        {
            SplitFixedSide.First => BesideFixed(along, LaidOutExtent(split, along, gap), gap, fixedFirst: true),
            SplitFixedSide.Second => BesideFixed(along, LaidOutExtent(split, along, gap), gap, fixedFirst: false),
            _ => Scale(TileDropRatio.GutterBand(split.SplitRatio), along)
        };

        return vertical
            ? new Rect(room.X + start, room.Y, size, room.Height)
            : new Rect(room.X, room.Y + start, room.Width, size);
    }

    /// <summary>The pixels the fixed side will actually be laid out at once the newcomer is in.</summary>
    /// <remarks>The cap <c>TileNodeView</c> puts on the fixed pane (<see cref="TileMinimumSize.FixedMaximum"/>),
    /// with the flexible side's minimum counted as it will be after the drop — its tiles, a gutter and the
    /// newcomer's own minimum. Without it a fixed size wider than the split draws a band of nothing where
    /// the drop still splits the room the grid keeps for the other side.</remarks>
    private static double LaidOutExtent(SplitTileNodeViewModel split, double along, double gap)
    {
        var (fixedChild, flexibleChild) = split.FixedSide == SplitFixedSide.First
            ? (split.First, split.Second)
            : (split.Second, split.First);
        Func<TileNodeViewModel?, double, double> minimumOf = split.Orientation == Orientation.Vertical
            ? TileMinimumSize.Width
            : TileMinimumSize.Height;

        var available = along - gap;
        var (fixedMinimum, flexibleMinimum) = TileMinimumSize.Fit(
            minimumOf(fixedChild, gap),
            minimumOf(flexibleChild, gap) + gap + TileMinimumSize.LeafSize,
            available);

        return Math.Min(split.FixedExtent, TileMinimumSize.FixedMaximum(fixedMinimum, flexibleMinimum, available));
    }

    /// <summary>The newcomer's band beside a fixed side: its share of the flexible side, placed after
    /// the fixed pixels and the gutter when the fixed side comes first.</summary>
    private static (double Start, double Size) BesideFixed(double along, double extent, double gap, bool fixedFirst)
    {
        var flexible = Math.Max(0, along - extent - gap);
        var flexibleStart = fixedFirst ? Math.Min(along, extent + gap) : 0;
        var (start, size) = Scale(TileDropRatio.BesideFixedBand(newcomerFirst: fixedFirst), flexible);
        return (flexibleStart + start, size);
    }

    private static (double Start, double Size) Scale((double Start, double Size) share, double along) =>
        (share.Start * along, share.Size * along);
}
