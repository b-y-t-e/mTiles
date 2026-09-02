namespace mTiles.ViewModels;

/// <summary>
/// Tile content that wants to know when it becomes the active tile.
/// </summary>
/// <remarks>
/// <para>For the one thing a tile cannot learn any other way: something outside it changed while it was
/// not being looked at. The Goal tile is the case that earned the interface — what it offers depends on
/// whether the working tree has uncommitted changes, and those are made in the terminal tile next door,
/// so between the moments it asks the answer can be stale in both directions: buttons missing from a
/// tree that has since acquired changes, or offered over one that has since been committed.</para>
/// <para>Activation rather than a watcher or a timer: it is the gesture that precedes using the tile, it
/// costs one call at the moment somebody is about to read the answer, and a filesystem watcher over an
/// entire worktree per tile — or a poll per tile for the whole session — buys freshness nobody is
/// looking at. A tile with nothing to re-read implements nothing and is asked nothing.</para>
/// </remarks>
public interface IActivatableTile : ITile
{
    /// <summary>This tile has just become the active one. Called on the UI thread, and only on the
    /// transition — a tile that is already active is not told again.</summary>
    void OnActivated();
}
