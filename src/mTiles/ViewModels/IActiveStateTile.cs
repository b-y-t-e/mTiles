namespace mTiles.ViewModels;

/// <summary>
/// Tile content that needs to know, at any moment, whether it is the active tile — both ways in.
/// </summary>
/// <remarks>
/// <para>A separate interface from <see cref="IActivatableTile"/>, which is told only on the way in:
/// that one re-reads something stale, and a tile that implements it has nothing to do on the way out.
/// This one holds a state, and a state that is only ever set to true is not one.</para>
/// <para>The terminal agent tile is the case that earned it. Its agent's session store is one directory
/// per workspace, so a conversation begun by <c>/clear</c> in one tile is seen by the watcher of every
/// tile of the same agent there — and by nothing that says which of them typed it. The tile being typed
/// into is the active one, so only the active tile may take a conversation it did not start with.</para>
/// </remarks>
public interface IActiveStateTile : ITile
{
    /// <summary>Whether this tile is the active one changed. Called on the UI thread, on every transition.
    /// </summary>
    void OnActiveChanged(bool isActive);
}
