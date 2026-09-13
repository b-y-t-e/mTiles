namespace mTiles.ViewModels;

// Per-workspace scope ensuring only one tile is active at a time.
// SuppressActivation prevents GotFocus cascades during programmatic Focus() calls.
public sealed class TileActivationScope
{
    /// <summary>Raised with the tile that became active, or with null when none is.</summary>
    public event Action<LeafTileNodeViewModel?>? ActiveTileChanged;

    private int _suppressCount;

    public bool IsSuppressed => _suppressCount > 0;

    /// <summary>The tile last activated in this scope — kept through <see cref="Deactivate"/>.</summary>
    /// <remarks>What a layout writes down as its active tile and what the scope comes back to. Not the
    /// leaves' <c>IsActive</c>, which the other level's activation clears: saved from that, a workspace
    /// left while a window note had the keyboard would reopen with no tile to return to. May name a tile
    /// that has since left its tree; <see cref="ActiveTileTracker"/> is what answers only for one inside.
    /// </remarks>
    public LeafTileNodeViewModel? LastActivated { get; private set; }

    public void Activate(LeafTileNodeViewModel tile)
    {
        if (_suppressCount != 0) return;

        LastActivated = tile;
        ActiveTileChanged?.Invoke(tile);
    }

    /// <summary>Names the tile this scope comes back to without activating it — a layout being restored.
    /// </summary>
    public void Remember(LeafTileNodeViewModel? tile) => LastActivated = tile;

    /// <summary>Leaves no tile in this scope active, while still remembering <see cref="LastActivated"/>.
    /// </summary>
    /// <remarks>What the window does to one level of tiles when the keyboard goes to the other: the
    /// window's layout and a workspace each have a scope, and without this both would show an active tile
    /// at once — two outlines, and nothing saying which one a shortcut or a dictated sentence reaches.
    /// Not held back by <see cref="SuppressActivation"/>, which exists to stop focus cascades inside one
    /// scope and has nothing to say about the other.</remarks>
    public void Deactivate() => ActiveTileChanged?.Invoke(null);

    public IDisposable SuppressActivation()
    {
        _suppressCount++;
        return new Suppressor(this);
    }

    private sealed class Suppressor(TileActivationScope scope) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            scope._suppressCount--;
        }
    }
}
