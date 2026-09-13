using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Metadata;
using Avalonia.VisualTree;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// Where a tile tree's drops arrive, and the one thing that decides where a dragged tile would land.
/// </summary>
/// <remarks>
/// <para><b>One per tree, and the only control in that tree carrying <c>DragDrop.AllowDrop</c>.</b>
/// Three kinds of target sit under the pointer at different moments — the tree's own outer band, the
/// gutter of a split, a tile — and they are <b>ranked, not weighed</b>: the outer band silences the
/// gutter and the gutter silences the tile, the same shape as the activity sources, and for the same
/// reason. Two of them answering at once is two hints painted at once and a drop whose result depends on
/// which handler ran last.</para>
/// <para><b>Surfaces nest, and each answers only for its own tree.</b> A drag from another tree is left
/// unhandled rather than refused, so the event carries on bubbling to the surface that does own it —
/// which is what lets one tree be drawn inside another without either knowing the other is there. For
/// the same reason a gutter or a tile under the pointer that belongs to a tree nested inside this one is
/// walked past rather than taken, and the walk stops at this surface.</para>
/// <para>The tile's hint is drawn by the tile (<see cref="ITileDropTarget"/>): it belongs inside the
/// card's own clip. What this surface draws is the other two bands, in the coordinates of the tree it is
/// laid over.</para>
/// </remarks>
public class TileDropSurface : Border
{
    private readonly Grid _layers = new();
    private readonly Border _hint = new()
    {
        IsVisible = false,
        IsHitTestVisible = false,
        ZIndex = 200,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        BorderThickness = new Thickness(2)
    };

    /// <summary>The target currently showing a hint, so it can be told to stop when the pointer moves on.</summary>
    private ITileDropTarget? _hintedTarget;

    private TileNodeView? _tree;

    public TileDropSurface()
    {
        ReadRoot = () => _tree?.DataContext as TileNodeViewModel;
        DragDrop.SetAllowDrop(this, true);
        _hint.Bind(CornerRadiusProperty, _hint.GetResourceObservable("RadiusTile"));
        _layers.Children.Add(_hint);
        Child = _layers;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>The view drawing the tree whose drops this surface answers.</summary>
    [Content]
    public TileNodeView? Tree
    {
        get => _tree;
        set
        {
            if (ReferenceEquals(_tree, value)) return;
            if (_tree is not null) _layers.Children.Remove(_tree);
            _tree = value;
            if (_tree is not null) _layers.Children.Insert(0, _tree);
        }
    }

    /// <summary>Reads the tree's current root.</summary>
    /// <remarks>A function, because the root is replaced by the edits themselves — an edge drop reads
    /// it again after its detach — and a value captured when the surface was built would describe a
    /// node that is no longer in the tree. Defaults to what the tree view is showing; the owner of the
    /// tree sets it where it can say so more directly.</remarks>
    public Func<TileNodeViewModel?> ReadRoot { get; set; }

    /// <summary>
    /// The room a dropped tile is given along the split a drop creates — pixels or a share — or null for
    /// the share each drop has always given.
    /// </summary>
    /// <remarks>Asked with the tile and the orientation of the split it would be put into, because
    /// what a size means depends on the axis — a list is as wide as a name when it stands beside
    /// the layout and one row tall when it lies along it. Null by default, which is every workspace: its
    /// tiles share their room.</remarks>
    internal Func<LeafTileNodeViewModel, Orientation, TileDropSize?> SizeFor { get; set; } = static (_, _) => null;

    /// <summary>The colour this surface's hints are painted in, bands and tiles alike.</summary>
    /// <remarks>The surface's to say because the surface is the level: a tile under the pointer is told
    /// the colour rather than working it out, since the same tile control is drawn at both levels.
    /// </remarks>
    public string HintBrushKey { get; set; } = DropHintBrushes.WorkspaceKey;

    /// <summary>Everything this surface has drawn for a drag, put away.</summary>
    internal void ClearDropHints()
    {
        _hintedTarget?.HideDropOverlay();
        _hintedTarget = null;
        _hint.IsVisible = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = Resolve(e);
        if (target.Kind == TileDropKind.None)
        {
            // Not handled: a drag from another tree carries on bubbling to the surface that owns it, and
            // that surface's answer is written after this one.
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

        if (TileDragSession.Source is not { } source) return;

        switch (target.Kind)
        {
            case TileDropKind.RootEdge:
                TileTreeEdits.ExecuteRootEdge(source, ReadRoot, target.Zone, SizeForZone(source, target.Zone));
                break;

            case TileDropKind.Gutter when target.Split is { } split:
                TileTreeEdits.ExecuteGutter(source, split, SizeFor(source, split.Orientation));
                break;

            case TileDropKind.Leaf when target.Target?.DropNode is { } leaf:
                TileTreeEdits.Execute(source, leaf, target.Zone, SizeForZone(source, target.Zone));
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Where the pointer is, in the terms the three targets are ranked by.</summary>
    internal readonly record struct DropTarget(
        TileDropKind Kind,
        DropZone Zone,
        ITileDropTarget? Target = null,
        SplitTileNodeViewModel? Split = null,
        TileNodeView? SplitView = null);

    private DropTarget Resolve(DragEventArgs e)
    {
        if (_tree is not { } tree) return default;
        if (TileDragSession.Source is not { } source) return default;

        if (ReadRoot() is not { } root || !TileDragSession.IsFrom(root)) return default;

        var size = tree.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return default;

        var pos = e.GetPosition(tree);

        // Ranked highest because it is the only one of the three with nowhere else to live: a surface's
        // padding can be zero on a side, so the band has to overlap the outermost tile and therefore has
        // to outrank it.
        var edge = TileDropGeometry.GetRootEdge(pos, size);
        if (edge != DropZone.None && root is SplitTileNodeViewModel)
            return new DropTarget(TileDropKind.RootEdge, edge);

        if (tree.InputHitTest(pos) is not Visual hit) return default;

        return TargetUnder(hit, this, root, source,
            control => TileDropGeometry.GetDropZone(e.GetPosition(control), control.Bounds.Size));
    }

    /// <summary>The gutter or tile of <paramref name="root"/>'s tree nearest above <paramref name="hit"/>.</summary>
    /// <remarks>A gutter or tile of a tree nested inside this one is walked past, and the walk stops at
    /// <paramref name="stopAt"/>, so nothing above the surface that asked is ever taken.</remarks>
    internal static DropTarget TargetUnder(
        Visual hit, Visual stopAt, TileNodeViewModel root, LeafTileNodeViewModel source,
        Func<Control, DropZone> zoneOf)
    {
        foreach (var node in hit.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, stopAt)) break;

            if (node is GridSplitter splitter && splitter.Classes.Contains("tile-gutter"))
            {
                // The splitter inherits its split from the view whose grid holds it.
                if (splitter.DataContext is not SplitTileNodeViewModel split) continue;
                if (!ReferenceEquals(TileTreeEdits.RootOf(split), root)) continue;

                return GutterTarget(split, source, splitter);
            }

            if (node is ITileDropTarget { DropNode: { } leaf } target and Control control)
            {
                if (!ReferenceEquals(TileTreeEdits.RootOf(leaf), root)) continue;
                if (ReferenceEquals(leaf, source)) return default;

                var zone = zoneOf(control);
                return TileTreeEdits.FixedSplitAcross(leaf, zone) is { } fixedSplit
                    ? GutterTarget(fixedSplit, source, control)
                    : new DropTarget(TileDropKind.Leaf, zone, Target: target);
            }
        }

        return default;
    }

    /// <summary>A drop on <paramref name="split"/>'s gutter, found from a control drawn inside that split.</summary>
    private static DropTarget GutterTarget(SplitTileNodeViewModel split, LeafTileNodeViewModel source, Visual inside)
    {
        split = TileTreeEdits.GutterSplitFor(split);

        // The gesture asks for a layout that is already on screen, so it is refused here rather than
        // left to look like a drop that did nothing.
        if (ReferenceEquals(split.First, source) || ReferenceEquals(split.Second, source))
            return default;

        var owner = inside.GetVisualAncestors().OfType<TileNodeView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, split));
        return new DropTarget(TileDropKind.Gutter, DropZone.Center, Split: split, SplitView: owner);
    }

    private void ShowHint(DropTarget target)
    {
        TileDragSession.Hinting(ClearDropHints);

        if (!ReferenceEquals(_hintedTarget, target.Target))
        {
            _hintedTarget?.HideDropOverlay();
            _hintedTarget = null;
        }

        if (target.Kind == TileDropKind.Leaf && target.Target is { } leaf)
        {
            if (LeafEdgeBand(target) is { } fixedBand)
            {
                _hintedTarget = null;
                leaf.HideDropOverlay();
                Paint(fixedBand);
                return;
            }

            _hint.IsVisible = false;
            _hintedTarget = leaf;
            leaf.ShowDropOverlay(target.Zone, HintBrushKey);
            return;
        }

        var rect = target.Kind switch
        {
            TileDropKind.RootEdge => EdgeBand(target.Zone),
            TileDropKind.Gutter => GutterBand(target),
            _ => (Rect?)null
        };

        if (rect is not { } band)
        {
            _hint.IsVisible = false;
            return;
        }

        Paint(band);
    }

    /// <summary>What <see cref="SizeFor"/> answers for a drop into <paramref name="zone"/>.</summary>
    private TileDropSize? SizeForZone(LeafTileNodeViewModel source, DropZone zone) => zone switch
    {
        DropZone.Left or DropZone.Right => SizeFor(source, Orientation.Vertical),
        DropZone.Top or DropZone.Bottom => SizeFor(source, Orientation.Horizontal),
        _ => null
    };

    /// <summary>The room a tile dropped on the tree's edge will actually take.</summary>
    private Rect? EdgeBand(DropZone zone) =>
        TileDragSession.Source is { } source
            ? TileDropGeometry.EdgeBand(
                _tree!.Bounds.Size, zone, SizeForZone(source, zone), TileNodeView.TileGap, MinimumAlong(ReadRoot(), zone))
            : null;

    /// <summary>The minimum, along the axis a drop into <paramref name="zone"/> divides, of <paramref name="node"/>.</summary>
    private static double MinimumAlong(TileNodeViewModel? node, DropZone zone) =>
        zone is DropZone.Left or DropZone.Right
            ? TileMinimumSize.Width(node, TileNodeView.TileGap)
            : TileMinimumSize.Height(node, TileNodeView.TileGap);

    /// <summary>The room a tile dropped on a tile's edge takes when it is given a size — pixels or a share.</summary>
    /// <remarks>Null when it is not, and the tile draws its own hint: the band is the same
    /// <see cref="TileDropGeometry.EdgeBand"/> the tree's edge draws, inside the tile the drop splits.</remarks>
    private Rect? LeafEdgeBand(DropTarget target)
    {
        if (TileDragSession.Source is not { } source) return null;
        if (SizeForZone(source, target.Zone) is not { IsUsable: true } size) return null;
        if (BoundsOf(target.Target as Visual) is not { } area) return null;

        return TileDropGeometry.EdgeBand(
                area.Size, target.Zone, size, TileNodeView.TileGap, MinimumAlong(target.Target?.DropNode, target.Zone))
            .Translate(area.Position);
    }

    /// <summary>The room a tile dropped between two others will actually take.</summary>
    /// <remarks>Drawn from the same rule the drop itself runs on
    /// (<see cref="TileDropGeometry.GutterBand"/>), rather than as a marker on the gutter sized by eye: a
    /// hint that is not the resulting geometry is a hint that stops being true the moment somebody
    /// drags a splitter.</remarks>
    private Rect? GutterBand(DropTarget target)
    {
        if (target.Split is not { } split || target.SplitView is not { } view) return null;
        if (TileDragSession.Source is not { } source) return null;
        if (BoundsOf(view) is not { } area) return null;

        var room = RoomAfterDetach(source, split, view, area);
        return TileDropGeometry.GutterBand(room, split, TileNodeView.TileGap, SizeFor(source, split.Orientation));
    }

    /// <summary>The bounds a split will have once the dragged tile has been taken out of the tree.</summary>
    /// <remarks>The drop detaches first, so a split lying under the source's sibling grows into the room
    /// the source leaves behind — drawn against today's bounds, the hint would show half of what the tile
    /// actually gets.</remarks>
    private Rect RoomAfterDetach(
        LeafTileNodeViewModel source, SplitTileNodeViewModel split, TileNodeView view, Rect area)
    {
        if (TileTreeEdits.LiftedByDetach(source, split) is not { } lifted) return area;
        if (source.Parent is not { } vacated) return area;

        return BoundsOf(AncestorViewOf(view, lifted)) is { } liftedBounds
               && BoundsOf(AncestorViewOf(view, vacated)) is { } vacatedBounds
            ? TileDropGeometry.AfterDetach(area, liftedBounds, vacatedBounds)
            : area;
    }

    /// <summary>The view drawing <paramref name="node"/>, found by walking up from a view inside it.</summary>
    /// <remarks>Up, never down: this runs on every <c>DragOver</c>, which arrives with every pointer move,
    /// and a walk down the tree visits everything the tiles draw — AvaloniaEdit's lines, a Goal
    /// transcript — twice a move. Both views wanted here are ancestors of the target split's own view,
    /// because the split lies under the lifted sibling and the sibling under the split being vacated, so
    /// the walk costs the depth of the tree and cannot land on a view outside the branch that grows.</remarks>
    private static TileNodeView? AncestorViewOf(TileNodeView from, TileNodeViewModel node) =>
        from.GetSelfAndVisualAncestors().OfType<TileNodeView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, node));

    private Rect? BoundsOf(Visual? view) =>
        view?.TranslatePoint(default, _tree!) is { } origin ? new Rect(origin, view.Bounds.Size) : null;

    /// <summary>Puts the band on screen, in the same brushes a tile's own hint uses.</summary>
    private void Paint(Rect band)
    {
        var (fill, outline) = DropHintBrushes.For(this, HintBrushKey);

        _hint.Background = fill;
        _hint.BorderBrush = outline;

        var size = _tree!.Bounds.Size;
        _hint.Margin = new Thickness(
            band.X,
            band.Y,
            Math.Max(0, size.Width - band.Right),
            Math.Max(0, size.Height - band.Bottom));
        _hint.IsVisible = true;
    }
}
