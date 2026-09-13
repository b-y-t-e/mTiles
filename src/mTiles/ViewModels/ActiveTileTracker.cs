using System.ComponentModel;

namespace mTiles.ViewModels;

/// <summary>
/// Which tile of one tree a window-level command acts on, and when that answer — or that tile's own
/// state — changes.
/// </summary>
/// <remarks>
/// Shared by a workspace and the window's layout, so both levels resolve the active tile and report its
/// changes by one rule: a tile the phone redraws on is a tile it redraws on at either level.
/// </remarks>
public sealed class ActiveTileTracker
{
    private readonly TileActivationScope _scope;
    private readonly Func<TileNodeViewModel?> _root;

    /// <param name="scope">The tree's activation scope, which remembers the tile last activated.</param>
    /// <param name="root">The tree's current root, read on every question.</param>
    public ActiveTileTracker(TileActivationScope scope, Func<TileNodeViewModel?> root)
    {
        _scope = scope;
        _root = root;
    }

    /// <summary>Raised when the active tile changes, or when that tile's actions or name do.</summary>
    public event Action? Changed;

    /// <summary>The tile last activated, and <b>nothing</b> if it has left the tree.</summary>
    /// <remarks>A detached leaf's content has been disposed, and falling back to some other tile sends a
    /// dictated sentence — with auto-Enter, a command — to a tile nobody chose.</remarks>
    public LeafTileNodeViewModel? ActiveTile =>
        _scope.LastActivated is { } leaf && TileTreeEdits.LeavesOf(_root()).Contains(leaf) ? leaf : null;

    public void Watch(LeafTileNodeViewModel leaf)
    {
        leaf.PropertyChanged -= OnLeafPropertyChanged;
        leaf.PropertyChanged += OnLeafPropertyChanged;
    }

    public void Unwatch(LeafTileNodeViewModel leaf) => leaf.PropertyChanged -= OnLeafPropertyChanged;

    /// <summary>Reports a change nothing on a leaf announces — a closed tile or a rebuilt tree.</summary>
    public void RaiseChanged() => Changed?.Invoke();

    private void OnLeafPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LeafTileNodeViewModel leaf) return;

        if (BecameActive(leaf, e) || ChangedWhileActive(leaf, e))
            RaiseChanged();
    }

    private static bool BecameActive(LeafTileNodeViewModel leaf, PropertyChangedEventArgs e) =>
        e.PropertyName == nameof(LeafTileNodeViewModel.IsActive) && leaf.IsActive;

    /// <summary>The active tile's own list, or the name it is offered under.</summary>
    /// <remarks>The leaf republishes its Actions on any content change at all, deliberately, so this needs
    /// no list of the properties each kind computes its enabled flags from — and a tile nobody is aimed at
    /// raises nothing here.</remarks>
    private bool ChangedWhileActive(LeafTileNodeViewModel leaf, PropertyChangedEventArgs e) =>
        e.PropertyName is nameof(LeafTileNodeViewModel.Actions) or nameof(LeafTileNodeViewModel.TileName)
        && ReferenceEquals(leaf, ActiveTile);
}
