using Avalonia;
using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Views;

internal enum DropZone { None, Left, Right, Top, Bottom, Center }

internal static class TileDragDrop
{
    public const string DataFormat = "application/x-mtiles-tile";
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
            SwapPlaces(source, target);
            return;
        }

        MoveToEdge(source, target, zone);
    }

    /// <summary>
    /// Swaps two tiles by exchanging their places in the tree.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing inside a tile moves.</b> The obvious swap — trading <c>Content</c>, the kind,
    /// the name and the <c>TileId</c> between two leaves — parts a tile's content from the object its
    /// identity is read through: a terminal resolves <c>${tileId}</c> through the function its
    /// <see cref="Services.Tiles.TileContext"/> was built with, and that function answers with the id of
    /// the leaf that <em>created</em> it, not of whichever leaf is holding it now. Four values changed
    /// hands and the fifth, the closure, could not — so both terminals came out reading the other tile's
    /// id, and "Restart shell" relaunched each of them under its neighbour's session.
    /// </para>
    /// <para>Exchanging the two leaves' slots in their parents says the same thing on screen and leaves
    /// every pairing intact: content, id, name and the leaf that owns all three travel together because
    /// they never come apart. It is also why nothing has to be re-stamped afterwards — there is no
    /// copying here for anyone to forget to keep in step.</para>
    /// <para>A leaf with no split above it is the whole tree and the only tile in it, so the only drop
    /// that could reach one is a drag between two workspaces — which no window shows at once. Left
    /// alone rather than handled: moving a tile into a tree another workspace configured is a larger
    /// question than a drop gesture answers.</para>
    /// </remarks>
    private static void SwapPlaces(LeafTileNodeViewModel a, LeafTileNodeViewModel b)
    {
        if (a.Parent is not SplitTileNodeViewModel parentOfA ||
            b.Parent is not SplitTileNodeViewModel parentOfB)
            return;

        // Both slots are read before either is written: with one parent holding both tiles, writing the
        // first would otherwise answer the second question wrongly.
        var aWasFirst = parentOfA.First == a;
        var bWasFirst = parentOfB.First == b;

        a.Parent = parentOfB;
        b.Parent = parentOfA;

        if (aWasFirst) parentOfA.First = b; else parentOfA.Second = b;
        if (bWasFirst) parentOfB.First = a; else parentOfB.Second = a;

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
