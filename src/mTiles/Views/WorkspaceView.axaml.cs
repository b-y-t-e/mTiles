using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The workspace's canvas, and the one thing that decides where a dragged tile would land.
/// </summary>
/// <remarks>
/// <para>Every drop in a workspace arrives here, because this is the only control with
/// <c>DragDrop.AllowDrop</c> on it. Three kinds of target sit under the pointer at different moments —
/// the workspace's own outer band, the gutter of a split, a tile — and they are <b>ranked, not
/// weighed</b>: the outer band silences the gutter and the gutter silences the tile, the same shape as
/// the activity sources, and for the same reason. Two of them answering at once is two hints painted
/// at once and a drop whose result depends on which handler ran last.</para>
/// <para>The tile's hint is still drawn by the tile: it belongs inside the card's own clip, and nothing
/// out here knows that radius. What this view draws is the other two, in the coordinates of the tile
/// tree it is laid over.</para>
/// </remarks>
public partial class WorkspaceView : UserControl
{
    /// <summary>The tile currently showing a hint, so it can be told to stop when the pointer moves on.</summary>
    private LeafTileView? _hintedLeaf;

    public WorkspaceView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Everything this view has drawn for a drag, put away.</summary>
    /// <remarks>Also called by the drag's own source when it finishes, whatever became of it: a drag
    /// abandoned with Escape is not guaranteed to raise <c>DragLeave</c> on any backend.</remarks>
    internal void ClearDropHints()
    {
        _hintedLeaf?.HideDropOverlay();
        _hintedLeaf = null;
        DropHint.IsVisible = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = Resolve(e);
        if (target.Kind == TileDropKind.None)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropHints();
            return;
        }

        ShowHint(target);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ClearDropHints();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var target = Resolve(e);
        ClearDropHints();

        if (TileDragDrop.DragSource is not { } source) return;

        switch (target.Kind)
        {
            case TileDropKind.WorkspaceEdge:
                TileDragDrop.ExecuteWorkspaceEdge(
                    source, () => (DataContext as WorkspaceViewModel)?.RootTile, target.Zone);
                break;

            case TileDropKind.Gutter when target.Split is { } split:
                TileDragDrop.ExecuteGutter(source, split);
                break;

            case TileDropKind.Leaf when target.Leaf?.DataContext is LeafTileNodeViewModel leaf:
                TileDragDrop.Execute(source, leaf, target.Zone);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Where the pointer is, in the terms the three targets are ranked by.</summary>
    private readonly record struct DropTarget(
        TileDropKind Kind,
        DropZone Zone,
        LeafTileView? Leaf = null,
        SplitTileNodeViewModel? Split = null,
        TileNodeView? SplitView = null);

    private DropTarget Resolve(DragEventArgs e)
    {
        if (TileDragDrop.DragSource is not { } source) return default;

        var size = RootTileView.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return default;

        var pos = e.GetPosition(RootTileView);

        // Ranked highest because it is the only one of the three with nowhere else to live: the
        // workspace's padding is zero on the left, so the band has to overlap the outermost tile and
        // therefore has to outrank it.
        var edge = TileDragDrop.GetWorkspaceEdge(pos, size);
        if (edge != DropZone.None && (DataContext as WorkspaceViewModel)?.RootTile is SplitTileNodeViewModel)
            return new DropTarget(TileDropKind.WorkspaceEdge, edge);

        if (RootTileView.InputHitTest(pos) is not Visual hit) return default;

        foreach (var node in hit.GetSelfAndVisualAncestors())
        {
            if (node is GridSplitter splitter && splitter.Classes.Contains("tile-gutter"))
            {
                var owner = splitter.GetVisualAncestors().OfType<TileNodeView>().FirstOrDefault();
                if (owner?.DataContext is not SplitTileNodeViewModel split) return default;

                // The gesture asks for a layout that is already on screen, so it is refused here rather
                // than left to look like a drop that did nothing.
                if (ReferenceEquals(split.First, source) || ReferenceEquals(split.Second, source))
                    return default;

                return new DropTarget(TileDropKind.Gutter, DropZone.Center, Split: split, SplitView: owner);
            }

            if (node is LeafTileView leaf)
            {
                if (ReferenceEquals(leaf.DataContext, source)) return default;
                var local = e.GetPosition(leaf);
                return new DropTarget(
                    TileDropKind.Leaf, TileDragDrop.GetDropZone(local, leaf.Bounds.Size), Leaf: leaf);
            }
        }

        return default;
    }

    private void ShowHint(DropTarget target)
    {
        if (!ReferenceEquals(_hintedLeaf, target.Leaf))
        {
            _hintedLeaf?.HideDropOverlay();
            _hintedLeaf = null;
        }

        if (target.Kind == TileDropKind.Leaf && target.Leaf is { } leaf)
        {
            DropHint.IsVisible = false;
            _hintedLeaf = leaf;
            leaf.ShowDropOverlay(target.Zone);
            return;
        }

        var rect = target.Kind switch
        {
            TileDropKind.WorkspaceEdge => EdgeBand(target.Zone),
            TileDropKind.Gutter => GutterBand(target),
            _ => (Rect?)null
        };

        if (rect is not { } band)
        {
            DropHint.IsVisible = false;
            return;
        }

        Paint(band);
    }

    /// <summary>The room a tile dropped on the workspace's edge will actually take.</summary>
    private Rect EdgeBand(DropZone zone)
    {
        var size = RootTileView.Bounds.Size;
        var horizontal = zone is DropZone.Left or DropZone.Right;
        var (start, share) = TileDropRatio.EdgeBand(zone is DropZone.Left or DropZone.Top);

        return horizontal
            ? new Rect(start * size.Width, 0, share * size.Width, size.Height)
            : new Rect(0, start * size.Height, size.Width, share * size.Height);
    }

    /// <summary>The room a tile dropped between two others will actually take.</summary>
    /// <remarks>Drawn from the same rule the drop itself runs on
    /// (<see cref="TileDropRatio.GutterBand"/>), rather than as a marker on the gutter sized by eye: a
    /// hint that is not the resulting geometry is a hint that stops being true the moment somebody
    /// drags a splitter.</remarks>
    private Rect? GutterBand(DropTarget target)
    {
        if (target.Split is not { } split || target.SplitView is not { } view) return null;
        if (TileDragDrop.DragSource is not { } source) return null;
        if (BoundsOf(view) is not { } area) return null;

        var room = RoomAfterDetach(source, split, view, area);
        var (start, share) = TileDropRatio.GutterBand(split.SplitRatio);

        return split.Orientation == Orientation.Vertical
            ? new Rect(room.X + start * room.Width, room.Y, share * room.Width, room.Height)
            : new Rect(room.X, room.Y + start * room.Height, room.Width, share * room.Height);
    }

    /// <summary>The bounds a split will have once the dragged tile has been taken out of the tree.</summary>
    /// <remarks>The drop detaches first, so a split lying under the source's sibling grows into the room
    /// the source leaves behind — drawn against today's bounds, the hint would show half of what the tile
    /// actually gets.</remarks>
    private Rect RoomAfterDetach(
        LeafTileNodeViewModel source, SplitTileNodeViewModel split, TileNodeView view, Rect area)
    {
        if (TileDragDrop.LiftedByDetach(source, split) is not { } lifted) return area;
        if (source.Parent is not { } vacated) return area;

        return BoundsOf(AncestorViewOf(view, lifted)) is { } liftedBounds
               && BoundsOf(AncestorViewOf(view, vacated)) is { } vacatedBounds
            ? TileDragDrop.AfterDetach(area, liftedBounds, vacatedBounds)
            : area;
    }

    /// <summary>The view drawing <paramref name="node"/>, found by walking up from a view inside it.</summary>
    /// <remarks>Up, never down: this runs on every <c>DragOver</c>, which arrives with every pointer move,
    /// and a walk down the workspace visits everything the tiles draw — AvaloniaEdit's lines, a Goal
    /// transcript — twice a move. Both views wanted here are ancestors of the target split's own view,
    /// because the split lies under the lifted sibling and the sibling under the split being vacated, so
    /// the walk costs the depth of the tree and cannot land on a view outside the branch that grows.</remarks>
    private static TileNodeView? AncestorViewOf(TileNodeView from, TileNodeViewModel node) =>
        from.GetSelfAndVisualAncestors().OfType<TileNodeView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, node));

    private Rect? BoundsOf(Visual? view) =>
        view?.TranslatePoint(default, RootTileView) is { } origin ? new Rect(origin, view.Bounds.Size) : null;

    /// <summary>Puts the band on screen, in the same accent and weights a tile's own hint uses.</summary>
    private void Paint(Rect band)
    {
        var (fill, outline) = DropHintBrushes.For(this);

        DropHint.Background = fill;
        DropHint.BorderBrush = outline;
        DropHint.BorderThickness = new Thickness(2);

        var size = RootTileView.Bounds.Size;
        DropHint.HorizontalAlignment = HorizontalAlignment.Stretch;
        DropHint.VerticalAlignment = VerticalAlignment.Stretch;
        DropHint.Margin = new Thickness(
            band.X,
            band.Y,
            Math.Max(0, size.Width - band.Right),
            Math.Max(0, size.Height - band.Bottom));
        DropHint.IsVisible = true;
    }
}
