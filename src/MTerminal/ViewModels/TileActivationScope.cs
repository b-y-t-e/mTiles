namespace MTerminal.ViewModels;

// Per-workspace scope ensuring only one tile is active at a time.
// SuppressActivation prevents GotFocus cascades during programmatic Focus() calls.
public sealed class TileActivationScope
{
    public event Action<LeafTileNodeViewModel>? ActiveTileChanged;

    private int _suppressCount;

    public bool IsSuppressed => _suppressCount > 0;

    public void Activate(LeafTileNodeViewModel tile)
    {
        if (_suppressCount == 0)
            ActiveTileChanged?.Invoke(tile);
    }

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
