using Avalonia.Layout;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>Where on a target a dragged tile is dropped.</summary>
internal enum DropZone { None, Left, Right, Top, Bottom, Center }

/// <summary>
/// The changes a drag and drop makes to a tile tree: swap, split beside, insert between, put beside
/// everything, and take out.
/// </summary>
/// <remarks>
/// <para><b>Nothing here knows which tree it is editing.</b> A workspace's tiles and the window's own
/// tiles are the same node types, and the root is either read through <c>Parent</c> or handed in, so
/// the same edits serve both levels. That is the whole of what makes the gesture reusable: the
/// arbitration on screen is <c>Views.TileDropSurface</c>, and the geometry is
/// <c>Views.TileDropGeometry</c>.</para>
/// <para>In the view model layer rather than beside the views that call it, because closing a tile
/// takes it out of the tree through <see cref="DetachFromTree"/> too, and a view model reaching into
/// <c>Views/</c> for that was the one place this layer depended on the one above it.</para>
/// </remarks>
internal static class TileTreeEdits
{
    public static void Execute(LeafTileNodeViewModel source, LeafTileNodeViewModel target, DropZone zone)
    {
        if (source == target || zone == DropZone.None) return;

        if (zone == DropZone.Center)
            SwapPlaces(source, target);
        else
            MoveToEdge(source, target, zone);

        // A drop moves a tile between parents, which is the other way the soloed splits stop describing
        // the tree — the same reason DetachFromTree says so, said once for both drops.
        source.MaximizeScope?.ReviewLayout();
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

    /// <summary>
    /// Drops a tile onto a split's gutter: it goes between the two tiles that split holds.
    /// </summary>
    /// <remarks>
    /// <para>The tree is binary, so "three side by side" is a split inside a split — the newcomer and
    /// the old second child under the slot the second child had. What keeps that from reading as a
    /// nested pane is the pair of ratios (<see cref="TileDropRatio.Gutter"/>): the newcomer takes a
    /// third of the whole split and the two tiles already there keep their proportion to each other, so
    /// what the user sees is one row of three.</para>
    /// <para><b>A tile dropped on the gutter of its own split is left alone.</b> Detaching it would
    /// lift its sibling into the split's slot and take the split out of the tree, leaving this method
    /// inserting into a node nobody draws — and the gesture asks for a layout that is already on
    /// screen.</para>
    /// </remarks>
    public static void ExecuteGutter(LeafTileNodeViewModel source, SplitTileNodeViewModel split)
    {
        if (ReferenceEquals(split.First, source) || ReferenceEquals(split.Second, source)) return;
        if (!DetachFromTree(source)) return;

        // Both read after the detach, never before: lifting the source's sibling can have replaced
        // either of this split's children with it.
        if (split.Second is not { } second) return;
        var neighbour = FirstLeaf(split);

        var (outer, inner) = TileDropRatio.Gutter(split.SplitRatio);

        var inserted = new SplitTileNodeViewModel(split.Orientation, source, second)
        {
            Parent = split,
            LayoutChanged = split.LayoutChanged,
            SplitRatio = inner
        };

        source.Parent = inserted;
        second.Parent = inserted;

        // The same rule MoveToEdge follows, and for the same reason: the dropped tile belongs to this
        // tree now, so it is configured by whoever configures this tree rather than by copying whatever
        // callbacks somebody once listed here.
        if (neighbour?.ConfigureNewLeaf is { } configure)
            configure(source);
        else
            source.LayoutChanged = split.LayoutChanged;

        // The ratio first and the child second: only the child rebuilds the view, so assigning it last
        // is what lets one rebuild read both halves of the change.
        split.SplitRatio = outer;
        split.Second = inserted;

        source.LayoutChanged?.Invoke();
        source.MaximizeScope?.ReviewLayout();
    }

    /// <summary>
    /// Drops a tile onto the tree's outer band: a new column or row beside the whole layout.
    /// </summary>
    /// <remarks>
    /// <para>The root is read through a delegate rather than taken as an argument, and read
    /// <em>twice</em>, because detaching the source can replace it: with two tiles in the tree the
    /// survivor is lifted into the root's own slot, so a root captured before the detach is a node that
    /// is no longer in the tree.</para>
    /// <para>A tree whose root is a single tile is refused outright. There is nothing to put a
    /// column beside, and the tile's own edge zone already answers that gesture.</para>
    /// </remarks>
    public static void ExecuteRootEdge(
        LeafTileNodeViewModel source, Func<TileNodeViewModel?> readRoot, DropZone zone)
    {
        if (zone is DropZone.None or DropZone.Center) return;
        if (readRoot() is not SplitTileNodeViewModel) return;
        if (!DetachFromTree(source)) return;
        if (readRoot() is not { } root) return;

        var orientation = zone is DropZone.Left or DropZone.Right
            ? Orientation.Vertical : Orientation.Horizontal;
        var sourceFirst = zone is DropZone.Left or DropZone.Top;

        var first = sourceFirst ? (TileNodeViewModel)source : root;
        var second = sourceFirst ? root : source;

        var split = new SplitTileNodeViewModel(orientation, first, second)
        {
            SplitRatio = TileDropRatio.Edge(sourceFirst),
            LayoutChanged = root.LayoutChanged
        };

        first.Parent = split;
        second.Parent = split;

        // RootReplaced is the workspace's own "here is the new tree", and it configures every node under
        // it — the dropped tile included — so nothing here has to hand the source its callbacks back.
        source.RootReplaced?.Invoke(split);
        source.MaximizeScope?.ReviewLayout();
    }

    /// <summary>The root of the tree a node hangs in.</summary>
    /// <remarks>What a drop surface asks before it accepts anything: the window's tree and a workspace's
    /// tree are drawn one inside the other, so a pointer over a workspace tile is also over the window's
    /// surface, and a gesture is only ever answered by the surface whose tree the dragged tile — and the
    /// tile or gutter under the pointer — actually belongs to. Walked through <c>Parent</c> rather than
    /// held as a field, so a tile moved by any of the edits here needs nothing re-stamped.</remarks>
    public static TileNodeViewModel RootOf(TileNodeViewModel node)
    {
        while (node.Parent is { } parent)
            node = parent;
        return node;
    }

    /// <summary>
    /// The node a gutter drop lands in once the dragged tile has been taken out of the tree, or
    /// <c>null</c> when taking it out leaves that node where it is.
    /// </summary>
    /// <remarks>Every drop detaches its source first, and that lifts the source's sibling into the slot
    /// the two of them shared. A split anywhere under that sibling is therefore about to grow, and the
    /// hint has to be drawn against the room it will have rather than the room it has now.</remarks>
    public static TileNodeViewModel? LiftedByDetach(LeafTileNodeViewModel source, TileNodeViewModel target)
    {
        if (source.Parent is not SplitTileNodeViewModel parent) return null;

        var sibling = ReferenceEquals(parent.First, source) ? parent.Second : parent.First;
        for (TileNodeViewModel? node = target; node != null; node = node.Parent)
        {
            if (ReferenceEquals(node, sibling)) return sibling;
        }

        return null;
    }

    /// <summary>The first leaf under a node, used to ask a tree how it configures its tiles.</summary>
    private static LeafTileNodeViewModel? FirstLeaf(TileNodeViewModel? node) => node switch
    {
        LeafTileNodeViewModel leaf => leaf,
        SplitTileNodeViewModel split => FirstLeaf(split.First) ?? FirstLeaf(split.Second),
        _ => null
    };

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

        // The tile that stayed has just been lifted into its grandparent's slot, so the split that
        // held the two of them is out of the tree — and it may be one a maximized tile is being drawn
        // through. Told here rather than at each caller: this is the one method that takes a node out,
        // and a scope left describing a split nobody draws lights the header's "Exit full screen" over
        // an ordinary layout.
        node.MaximizeScope?.ReviewLayout();
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
