using CommunityToolkit.Mvvm.ComponentModel;

namespace mTiles.ViewModels;

public abstract partial class TileNodeViewModel : ObservableObject
{
    /// <summary>The split this node hangs under, or null when it is the workspace's whole tree.</summary>
    /// <remarks>It notifies, because moving a node changes what it can do rather than only where it is
    /// drawn: a leaf with no split above it already fills the workspace, so it has no full screen to go
    /// to, and the header reads that off <c>CanMaximize</c> long after the tile was built.</remarks>
    public TileNodeViewModel? Parent
    {
        get => _parent;
        set
        {
            if (ReferenceEquals(_parent, value)) return;
            _parent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFirstChild));
        }
    }

    private TileNodeViewModel? _parent;
    public bool IsFirstChild => Parent is SplitTileNodeViewModel split && split.First == this;

    public Action? LayoutChanged { get; set; }

    protected void NotifyLayoutChanged() => LayoutChanged?.Invoke();
}
