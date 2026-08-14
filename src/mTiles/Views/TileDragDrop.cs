using Avalonia;
using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Views;

internal enum DropZone { None, Left, Right, Top, Bottom, Center }

internal static class TileDragDrop
{
    public const string DataFormat = "application/x-mterminal-tile";
    public static LeafTileNodeViewModel? DragSource { get; set; }

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

    public static void Execute(LeafTileNodeViewModel source, LeafTileNodeViewModel target, DropZone zone)
    {
        if (source == target || zone == DropZone.None) return;

        if (zone == DropZone.Center)
        {
            SwapContent(source, target);
            return;
        }

        MoveToEdge(source, target, zone);
    }

    private static void SwapContent(LeafTileNodeViewModel a, LeafTileNodeViewModel b)
    {
        var (ac, at, an, ai) = (a.Content, a.ContentType, a.TileName, a.TileId);

        a.Content = b.Content;
        a.ContentType = b.ContentType;
        a.TileName = b.TileName;
        a.TileId = b.TileId;

        b.Content = ac;
        b.ContentType = at;
        b.TileName = an;
        b.TileId = ai;

        if (a.Content is TerminalTileViewModel ta) ta.TileId = a.TileId;
        if (b.Content is TerminalTileViewModel tb) tb.TileId = b.TileId;

        a.LayoutChanged?.Invoke();
    }

    public static bool DetachFromTree(LeafTileNodeViewModel node)
    {
        if (node.Parent is not SplitTileNodeViewModel parentSplit) return false;

        var sibling = parentSplit.First == node ? parentSplit.Second : parentSplit.First;
        if (sibling == null) return false;

        sibling.Parent = parentSplit.Parent;
        sibling.LayoutChanged = parentSplit.LayoutChanged;
        PropagateSiblingCallbacks(sibling, node);

        if (parentSplit.Parent is SplitTileNodeViewModel grandParent)
        {
            if (grandParent.First == parentSplit)
                grandParent.First = sibling;
            else
                grandParent.Second = sibling;
        }
        else
        {
            node.RootReplaced?.Invoke(sibling);
        }

        node.Parent = null;
        return true;
    }

    private static void MoveToEdge(LeafTileNodeViewModel source, LeafTileNodeViewModel target, DropZone zone)
    {
        if (!DetachFromTree(source)) return;

        // Insert source next to target
        var targetParent = target.Parent as SplitTileNodeViewModel;
        var orientation = zone is DropZone.Left or DropZone.Right
            ? Orientation.Vertical : Orientation.Horizontal;
        var sourceFirst = zone is DropZone.Left or DropZone.Top;

        var first = sourceFirst ? (TileNodeViewModel)source : target;
        var second = sourceFirst ? (TileNodeViewModel)target : source;

        var split = new SplitTileNodeViewModel(orientation, first, second)
        {
            Parent = target.Parent,
            LayoutChanged = target.LayoutChanged
        };

        first.Parent = split;
        second.Parent = split;

        // The dropped tile belongs to the target's tree now, so it is configured by whoever configures
        // that tree — not by copying the one callback this method happens to know about. Assigning
        // LayoutChanged alone was enough while that was all a tile needed; it stopped being enough the
        // moment tiles started subscribing to services.
        if (target.ConfigureNewLeaf is { } configure)
            configure(source);
        else
            source.LayoutChanged = target.LayoutChanged;

        if (targetParent != null)
        {
            if (targetParent.First == target)
                targetParent.First = split;
            else
                targetParent.Second = split;
        }
        else
        {
            target.RootReplaced?.Invoke(split);
        }

        source.LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Gives a tile the callbacks of the tree it now belongs to.
    /// </summary>
    /// <remarks>
    /// <para>Through <see cref="LeafTileNodeViewModel.ConfigureNewLeaf"/> — the workspace's own "here is
    /// what a tile needs" — rather than by copying the three that somebody once listed here. That list is
    /// the same shape as the one <c>Split</c> used to keep, and the same bug waiting: a callback added to
    /// the workspace and forgotten here leaves a re-parented tile without it, silently. Dictation was
    /// exactly that, in the other copy.</para>
    /// <para>Copying is kept as the fallback for a tree nobody configured, which in practice means a
    /// test — the same arrangement, and the same reason, as in <c>Split</c>.</para>
    /// </remarks>
    private static void PropagateSiblingCallbacks(TileNodeViewModel node, LeafTileNodeViewModel source)
    {
        if (node is LeafTileNodeViewModel leaf)
        {
            if (source.ConfigureNewLeaf is { } configure)
            {
                configure(leaf);
                return;
            }

            leaf.RootReplaced = source.RootReplaced;
            leaf.RootCleared = source.RootCleared;
            leaf.LayoutChanged = source.LayoutChanged;
        }
        else if (node is SplitTileNodeViewModel split)
        {
            split.LayoutChanged = source.LayoutChanged;
            if (split.First != null) PropagateSiblingCallbacks(split.First, source);
            if (split.Second != null) PropagateSiblingCallbacks(split.Second, source);
        }
    }
}
