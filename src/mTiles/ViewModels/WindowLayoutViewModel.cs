using Avalonia;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;

namespace mTiles.ViewModels;

/// <summary>
/// The window's own layout: the list of workspaces, the place the open workspace is drawn, and whatever
/// tiles have been put beside them.
/// </summary>
/// <remarks>
/// <para><b>The same tree a workspace has, one level up.</b> The node types, the drag and drop, the
/// fixed sides, the serializer and the full-screen scope are all the ones a workspace uses; what differs
/// is what is allowed in it (<see cref="TileCatalog"/> — no terminals, no agents, nothing that needs a
/// repository) and two tiles the tree must always hold, exactly once each.</para>
/// <para><b>Those two are permanent</b> (<see cref="ITileKind.IsPermanent"/>): they cannot be closed,
/// changed or chosen again, so the only way either could go missing is a file that says so. A file like
/// that is not mended piece by piece — it is replaced with <see cref="CreateDefaultLayout">the default
/// layout</see>, and the tiles it held are given up rather than rearranged into something nobody chose.
/// Their notes and lists stay on disk where they were.</para>
/// <para>Its directory stands in for a workspace directory wherever a tile asks for one
/// (<see cref="AppPaths.GetWindowDirectory"/>), so a note put beside the workspaces keeps its file
/// there by the rule that keeps a workspace's notes in the workspace.</para>
/// </remarks>
public sealed partial class WindowLayoutViewModel : ObservableObject, IDisposable
{
    /// <summary>The name the layout is saved under, inside the window's directory.</summary>
    internal const string LayoutId = "layout";

    private readonly PersistenceService _persistence;
    private readonly TileCatalog _catalog;
    private readonly TileContext _context;
    private readonly TileNameAllocator _names;
    private readonly TileTreeSerializer _serializer;
    private readonly TileActivationScope _activationScope = new();
    private readonly TileMaximizeScope _maximizeScope = new();

    /// <summary>Whether this file must be left as it was found — the rule a workspace follows for a tile
    /// of a kind this build does not have, for the same reason.</summary>
    private readonly bool _savingWouldLoseATile;

    /// <summary>How wide the list stood the last time it stood beside the layout.</summary>
    /// <remarks>Remembered so a list moved to the top and back comes back at the width it was given, not
    /// at the default.</remarks>
    private double _listWidth;

    [ObservableProperty]
    private TileNodeViewModel? _rootTile;

    private readonly ActiveTileTracker _activeTile;

    /// <summary>Raised when the window tile a window-level command acts on changes, or its own state does.
    /// </summary>
    /// <remarks>The workspace's event of the same name, one level up, for the same listeners.</remarks>
    public event Action? ActiveTileChanged
    {
        add => _activeTile.Changed += value;
        remove => _activeTile.Changed -= value;
    }

    /// <summary>The window tile the user last worked in, and nothing if it has left the tree.</summary>
    public LeafTileNodeViewModel? ActiveTile => _activeTile.ActiveTile;

    partial void OnRootTileChanged(TileNodeViewModel? value) => _activeTile.RaiseChanged();

    /// <param name="persistence">Where the layout is written — the window's own directory.</param>
    /// <param name="catalog">The kinds the window may hold, the two permanent ones among them.</param>
    /// <param name="directory">What the window's tiles use in place of a workspace directory.</param>
    /// <param name="listWidth">How wide the list stands when the file says nothing — the width the panel
    /// had before the window had a layout.</param>
    public WindowLayoutViewModel(PersistenceService persistence, SettingsService settings, TileCatalog catalog,
        string directory, double listWidth = WorkspacesTileKind.DefaultWidth, Action<int>? openSettings = null)
    {
        _activeTile = new ActiveTileTracker(_activationScope, () => RootTile);
        _persistence = persistence;
        _catalog = catalog;
        _names = new TileNameAllocator(catalog);
        _listWidth = listWidth > 0 && double.IsFinite(listWidth) ? listWidth : WorkspacesTileKind.DefaultWidth;
        _context = new TileContext(directory, settings, ScheduleSave, openSettings);
        _serializer = new TileTreeSerializer(catalog, _context, _names.Allocate, ConfigureLeafCallbacks,
            _activationScope);

        var state = persistence.LoadLayout(LayoutId);
        if (state?.RootTile is { } saved)
        {
            _names.RememberSaved(saved);
            var load = _serializer.Deserialize(saved, OnLayoutChanged);

            if (load.Root is { } root && HoldsEachPermanentTileOnce(root))
            {
                RootTile = root;
                _savingWouldLoseATile = load.HasUnknownKind;
                if (load.NeedsSave) ScheduleSave();
                RememberListWidth();
                return;
            }

            System.Diagnostics.Trace.TraceWarning(
                "The window layout does not hold the workspace list and the workspace view exactly once each; "
                + "the default layout is used instead.");
            DisposeTree(load.Root);
        }

        RootTile = CreateDefaultLayout();
    }

    /// <summary>How wide the list stands beside the layout, or last stood there if it is along an edge now.
    /// </summary>
    /// <remarks>What the window writes back into <c>AppSettings.WorkspacesPanelWidth</c> when it closes,
    /// so a build rolled back to before the window had a layout opens the panel at the width it was
    /// given here.</remarks>
    public double ListWidth
    {
        get
        {
            RememberListWidth();
            return _listWidth;
        }
    }

    /// <summary>Which of the window's tiles has the whole window to itself, if any.</summary>
    public TileMaximizeScope MaximizeScope => _maximizeScope;

    public TileActivationScope ActivationScope => _activationScope;

    /// <summary>
    /// The pixels a tile dropped on the window's layout is held at, for the split it would be put into.
    /// </summary>
    /// <remarks>
    /// <para>What the window's drop surface asks (<c>TileDropSurface.FixedExtentFor</c>). Only the list
    /// has an answer, and it depends on the axis: beside the layout it is as wide as it last was, along
    /// the top or the bottom it is one strip of tabs. Everything else shares its room.</para>
    /// <para>Asked before the drop is carried out, so a list dragged from one side to the other is still
    /// in its old split and keeps that split's width.</para>
    /// </remarks>
    public double? FixedExtentFor(LeafTileNodeViewModel tile, Orientation orientation)
    {
        if (!IsKind(tile, TileKindIds.Workspaces)) return null;
        if (orientation == Orientation.Horizontal) return WorkspacesTileKind.StripHeight;

        RememberListWidth();
        return _listWidth;
    }

    /// <summary>The size the window's layout is drawn at, which a new or dropped tile's room is decided by.
    /// </summary>
    /// <remarks>Told by the view whenever it changes. Nothing known yet is a size of nothing, which the
    /// rule reads as too small for pixels — the answer that cannot take the whole window.</remarks>
    public Size Size { get; set; }

    /// <summary>The room a tile dropped on the window's layout is given, for the split it would be put into.
    /// </summary>
    /// <remarks>
    /// <para>What the window's drop surface asks. The list has its fixed size on each axis
    /// (<see cref="FixedExtentFor"/>); the workspace shares its room, since it is what everything else
    /// makes room for; and every other window tile is narrow — pixels on a window with room for them twice
    /// over, a share on one without (<see cref="WindowTileSize"/>).</para>
    /// </remarks>
    internal TileDropSize? DropSizeFor(LeafTileNodeViewModel tile, Orientation orientation) => tile.KindId switch
    {
        TileKindIds.Workspaces => FixedExtentFor(tile, orientation) is { } pixels ? TileDropSize.InPixels(pixels) : null,
        TileKindIds.WorkspaceHost => null,
        _ => WindowTileSize.For(orientation, Size)
    };

    /// <summary>Puts a new tile of <paramref name="kindId"/> beside the whole layout, on its right.</summary>
    /// <remarks>The right rather than beside whichever tile is active, because the two tiles every
    /// window starts with have no header to split from and the workspace's own tiles belong to another
    /// tree. A permanent kind is refused: there is one of each already.</remarks>
    /// <returns>The new tile, or null when nothing was added.</returns>
    public LeafTileNodeViewModel? AddTile(string kindId)
    {
        if (_catalog.Kind(kindId) is not { IsPermanent: false } kind || RootTile is not { } root) return null;

        _maximizeScope.Restore();

        var leaf = CreateLeaf(kindId, kind);
        var split = new SplitTileNodeViewModel(Orientation.Vertical, root, leaf)
        {
            SplitRatio = TileDropRatio.Edge(newcomerFirst: false)
        };
        root.Parent = split;
        leaf.Parent = split;

        RootTile = ConfigureRoot(split);
        return leaf;
    }

    /// <summary>The list beside the layout on the left, at its width, and the workspace taking the rest.</summary>
    /// <remarks>The window as it looked before it had a layout, which is what the first run after the
    /// update has to look like — anybody who never moves anything must not be able to tell.</remarks>
    private TileNodeViewModel CreateDefaultLayout()
    {
        var list = CreateLeaf(TileKindIds.Workspaces, _catalog.Kind(TileKindIds.Workspaces)!);
        var host = CreateLeaf(TileKindIds.WorkspaceHost, _catalog.Kind(TileKindIds.WorkspaceHost)!);

        var split = new SplitTileNodeViewModel(Orientation.Vertical, list, host);
        split.Fix(SplitFixedSide.First, _listWidth);
        list.Parent = split;
        host.Parent = split;

        // Configured without being saved: a window that has never been rearranged has nothing to write
        // down, and the first launch after the update writing a file is a change nobody made.
        PropagateCallbacks(split);
        return split;
    }

    private LeafTileNodeViewModel CreateLeaf(string kindId, ITileKind kind)
    {
        var leaf = new LeafTileNodeViewModel(kindId, null, _context.WorkingDirectory,
            _activationScope, _catalog, _context, _names.Allocate)
        {
            TileName = _names.Allocate(kindId)
        };
        leaf.Content = kind.Create(_context with { TileId = () => leaf.TileId }, null);
        ConfigureLeafCallbacks(leaf);
        return leaf;
    }

    private static bool HoldsEachPermanentTileOnce(TileNodeViewModel root)
    {
        var leaves = TileTreeEdits.LeavesOf(root).ToList();
        return leaves.Count(leaf => IsKind(leaf, TileKindIds.Workspaces)) == 1
               && leaves.Count(leaf => IsKind(leaf, TileKindIds.WorkspaceHost)) == 1;
    }

    /// <summary>Whether <paramref name="leaf"/> is of <paramref name="kindId"/>, compared the way the
    /// catalog finds a kind — a file that spells an id in other letters still builds that kind, so it must
    /// also count as one.</summary>
    private static bool IsKind(LeafTileNodeViewModel leaf, string kindId) =>
        string.Equals(leaf.KindId, kindId, StringComparison.OrdinalIgnoreCase);

    private void ConfigureLeafCallbacks(LeafTileNodeViewModel leaf)
    {
        leaf.LayoutChanged = OnLayoutChanged;
        leaf.MaximizeScope = _maximizeScope;
        leaf.ConfigureNewLeaf = ConfigureLeafCallbacks;
        // A tile split off one of the window's is an empty tile about to become a note or a list — narrow,
        // by the rule a dropped one follows, rather than half of the tile it was split from.
        leaf.SizeForNewTile = orientation => WindowTileSize.For(orientation, Size);
        _activeTile.Watch(leaf);
        leaf.RootReplaced = newRoot => RootTile = ConfigureRoot(newRoot);

        // Nothing here can empty the tree — the two permanent tiles cannot be closed — so this is only
        // ever reached by a tree that was already wrong, and the default layout is the one it is owed.
        leaf.RootCleared = () =>
        {
            _maximizeScope.Restore();
            RootTile = CreateDefaultLayout();
            OnLayoutChanged();
        };
    }

    private TileNodeViewModel ConfigureRoot(TileNodeViewModel node)
    {
        node.Parent = null;
        PropagateCallbacks(node);
        OnLayoutChanged();
        return node;
    }

    private void PropagateCallbacks(TileNodeViewModel node)
    {
        node.LayoutChanged = OnLayoutChanged;
        if (node is LeafTileNodeViewModel leaf)
        {
            ConfigureLeafCallbacks(leaf);
        }
        else if (node is SplitTileNodeViewModel split)
        {
            if (split.First != null) PropagateCallbacks(split.First);
            if (split.Second != null) PropagateCallbacks(split.Second);
        }
    }

    private void OnLayoutChanged()
    {
        RememberListWidth();
        ScheduleSave();
    }

    /// <summary>Takes the list's width from the split it stands in, if it stands beside the layout.</summary>
    private void RememberListWidth()
    {
        var list = TileTreeEdits.LeavesOf(RootTile).FirstOrDefault(leaf => IsKind(leaf, TileKindIds.Workspaces));
        if (list?.Parent is SplitTileNodeViewModel { Orientation: Orientation.Vertical } split
            && split.IsFixed(list) && split.FixedExtent > 0)
            _listWidth = split.FixedExtent;
    }

    private void ScheduleSave()
    {
        if (_savingWouldLoseATile) return;
        _persistence.DebouncedSaveLayout(LayoutId, () => _serializer.Serialize(RootTile));
    }

    public void Dispose() => DisposeTree(RootTile);

    private void DisposeTree(TileNodeViewModel? node)
    {
        foreach (var leaf in TileTreeEdits.LeavesOf(node))
        {
            _activeTile.Unwatch(leaf);
            leaf.Dispose();
        }
    }
}
