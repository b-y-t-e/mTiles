namespace mTiles.ViewModels;

/// <summary>
/// Which tile, if any, has the whole workspace to itself.
/// </summary>
/// <remarks>
/// <para>One per workspace, exactly like <see cref="TileActivationScope"/>, and for the same reason:
/// "only one at a time" is a fact about the workspace and not about any tile in it, so a tile asking to
/// be maximized asks something that already knows what is maximized now.</para>
/// <para>It does not touch the tree. What it writes is <see cref="SplitTileNodeViewModel.Solo"/> on
/// every split between the root and the chosen leaf — each of those splits then draws that one child at
/// full size instead of two children and a splitter — and what it writes is undone by writing null back
/// to the same splits. Nothing is re-parented and no layout is saved, which is the whole point: the
/// maximized state is a way of looking at a workspace, not a change to it, and a crash or a restart
/// brings the tiles back exactly as they were arranged.</para>
/// <para>The path is walked upwards through <see cref="TileNodeViewModel.Parent"/> and then
/// <em>remembered</em>, rather than walked again to undo. A tile can be closed, dropped onto another or
/// split while it is maximized, and any of those leave the leaf pointing at parents it no longer has —
/// so restoring by walking again would leave a split soloed with nothing able to reach it, and half the
/// workspace invisible for the rest of the session.</para>
/// </remarks>
public sealed class TileMaximizeScope
{
    private readonly List<SplitTileNodeViewModel> _soloed = [];

    /// <summary>The tile filling the workspace, or null when the layout is showing normally.</summary>
    public LeafTileNodeViewModel? Maximized { get; private set; }

    /// <summary>Gives the workspace to this tile, or takes it back if this tile already has it.</summary>
    public void Toggle(LeafTileNodeViewModel leaf) =>
        Set(ReferenceEquals(Maximized, leaf) ? null : leaf);

    /// <summary>Puts the layout back, whatever was maximized.</summary>
    public void Restore() => Set(null);

    /// <summary>
    /// Puts the layout back if <paramref name="leaf"/> is the tile that was filling it.
    /// </summary>
    /// <remarks>What a tile calls on its way out — closing, or being split so that it is no longer the
    /// only thing worth looking at. Asking whether it is the maximized one first, because a tile closed
    /// in the background must not put another tile's full-screen view away.</remarks>
    public void Forget(LeafTileNodeViewModel leaf)
    {
        if (ReferenceEquals(Maximized, leaf))
            Restore();
    }

    /// <summary>
    /// Puts the soloed splits back in step with a tree that has been re-shaped underneath them.
    /// </summary>
    /// <remarks><para>Closing a tile does not only remove that tile: its sibling is lifted into the
    /// grandparent's slot, so a split this scope soloed can fall out of the tree entirely while the
    /// tile it was showing goes on filling the workspace. The remembered path then describes a shape
    /// nobody is drawing — the view correctly draws an ordinary split, and the header goes on offering
    /// "Exit full screen" for a tile that is not on its own any more.</para>
    /// <para>Answered by walking up from the maximized tile and comparing with what was remembered,
    /// which needs nothing of the tree beyond what <see cref="Toggle"/> already reads: no events, no
    /// registration, and a scope that still knows nothing about how tiles are closed or dropped. Where
    /// the two disagree the path is simply established again from where the tile is now, so a tile that
    /// still has the workspace keeps it and one whose splits are gone is left as the whole of a smaller
    /// tree.</para></remarks>
    public void ReviewLayout()
    {
        if (Maximized is { } leaf && !PathStillHolds(leaf))
            Set(leaf);
    }

    /// <summary>Whether the splits above the maximized tile are still the ones that were soloed.
    /// </summary>
    private bool PathStillHolds(LeafTileNodeViewModel leaf)
    {
        var depth = 0;
        for (TileNodeViewModel node = leaf; node.Parent is SplitTileNodeViewModel split; node = split)
        {
            if (depth >= _soloed.Count || !ReferenceEquals(_soloed[depth], split)) return false;
            if (!ReferenceEquals(split.Solo, node)) return false;
            depth++;
        }

        return depth == _soloed.Count;
    }

    private void Set(LeafTileNodeViewModel? leaf)
    {
        foreach (var split in _soloed)
            split.Solo = null;
        _soloed.Clear();

        if (Maximized is { } previous)
            previous.IsMaximized = false;

        Maximized = leaf;
        if (leaf is null) return;

        for (TileNodeViewModel node = leaf; node.Parent is SplitTileNodeViewModel split; node = split)
        {
            split.Solo = node;
            _soloed.Add(split);
        }

        // A leaf with no split above it is the whole tree and already has the workspace, so there is
        // nothing here to be in and nothing to come out of. Said by the scope rather than left to its
        // callers: the flag it would otherwise set is what the header draws "Exit full screen" from,
        // and a tile lifted into the root's slot while maximized reaches this the same way a press on a
        // lone tile would.
        if (_soloed.Count == 0)
        {
            Maximized = null;
            return;
        }

        leaf.IsMaximized = true;
    }
}
