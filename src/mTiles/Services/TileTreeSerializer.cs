using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services.Tiles;
using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// What a loaded tile tree needs from whoever asked for it.
/// </summary>
/// <param name="Root">The tree, or null when the file held nothing that could be built.</param>
/// <param name="ActiveLeaf">The tile that was active when it was saved.</param>
/// <param name="NeedsSave">Something was filled in that the file did not have — an old layout in the
/// pre-kind format, or a tile with no id.</param>
/// <param name="HasUnknownKind">
/// At least one leaf named a kind this build does not have.
/// <para><b>Nothing is saved when this is true.</b> That tile is shown as empty, and an empty tile
/// saved over the top of it is how a layout is lost for good: a kind written by a newer build, plus the
/// save a migration triggers, and the tile is gone from the file. Leaving the file alone costs the user
/// one wrong-looking tile until they are back on the build that wrote it.</para>
/// </param>
public sealed record TileTreeLoad(
    TileNodeViewModel? Root,
    LeafTileNodeViewModel? ActiveLeaf,
    bool NeedsSave,
    bool HasUnknownKind);

public sealed class TileTreeSerializer
{
    private readonly TileCatalog _catalog;
    private readonly TileContext _context;
    private readonly Func<string, string> _nameAllocator;
    private readonly Action<LeafTileNodeViewModel> _configureLeaf;
    private readonly TileActivationScope _activationScope;

    public TileTreeSerializer(
        TileCatalog catalog,
        TileContext context,
        Func<string, string> nameAllocator,
        Action<LeafTileNodeViewModel> configureLeaf,
        TileActivationScope activationScope)
    {
        _catalog = catalog;
        _context = context;
        _nameAllocator = nameAllocator;
        _configureLeaf = configureLeaf;
        _activationScope = activationScope;
    }

    /// <summary>What to write down for this tree.</summary>
    /// <remarks>Five <c>as</c> casts to concrete view models used to live here, one per kind, each
    /// pulling out the one field that kind needed. Each kind now answers for its own state, so this
    /// method knows nothing about any of them.</remarks>
    public TileNode? Serialize(TileNodeViewModel? vm)
    {
        return vm switch
        {
            LeafTileNodeViewModel leaf => new TileNode
            {
                IsLeaf = true,
                Kind = leaf.KindId,
                TileId = leaf.TileId,
                TileName = leaf.TileName,
                IsActive = leaf.IsActive,
                Settings = SaveState(leaf)
            },
            SplitTileNodeViewModel split => new TileNode
            {
                IsLeaf = false,
                SplitOrientation = split.Orientation,
                SplitRatio = split.SplitRatio,
                First = Serialize(split.First),
                Second = Serialize(split.Second)
            },
            _ => null
        };
    }

    private System.Text.Json.Nodes.JsonObject? SaveState(LeafTileNodeViewModel leaf) =>
        leaf.Content is { } content ? _catalog.Kind(leaf.KindId)?.Save(content) : null;

    public TileTreeLoad Deserialize(TileNode dto, Action scheduleSave)
    {
        LeafTileNodeViewModel? activeLeaf = null;
        var state = new LoadState();
        var root = DeserializeNode(dto, scheduleSave, ref activeLeaf, state);
        return new TileTreeLoad(root, activeLeaf, state.NeedsSave, state.HasUnknownKind);
    }

    /// <summary>What the walk collects on its way down, so the two answers do not travel as a pair of
    /// <c>ref bool</c> parameters through every level of the tree.</summary>
    private sealed class LoadState
    {
        public bool NeedsSave;
        public bool HasUnknownKind;
    }

    private TileNodeViewModel? DeserializeNode(TileNode dto, Action scheduleSave,
        ref LeafTileNodeViewModel? activeLeaf, LoadState state)
    {
        if (dto.IsLeaf)
            return DeserializeLeaf(dto, scheduleSave, ref activeLeaf, state);

        var first = DeserializeNode(dto.First!, scheduleSave, ref activeLeaf, state);
        var second = DeserializeNode(dto.Second!, scheduleSave, ref activeLeaf, state);
        if (first == null || second == null) return first ?? second;

        var split = new SplitTileNodeViewModel(dto.SplitOrientation, first, second)
        {
            SplitRatio = dto.SplitRatio,
            LayoutChanged = scheduleSave
        };
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    private LeafTileNodeViewModel DeserializeLeaf(TileNode dto, Action scheduleSave,
        ref LeafTileNodeViewModel? activeLeaf, LoadState state)
    {
        if (dto.IsLegacyFormat)
            state.NeedsSave = true;

        var kind = _catalog.Kind(dto.Kind);
        if (kind is null && dto.Kind.Length > 0)
        {
            // Named a kind nothing is registered under. The tile comes back empty rather than blank,
            // and the whole file is left as it is — see TileTreeLoad.HasUnknownKind.
            System.Diagnostics.Trace.TraceWarning(
                "Tile '{0}' names a kind this build does not have ('{1}'); it will show as empty and the "
                + "layout will not be rewritten.", dto.TileName, dto.Kind);
            state.HasUnknownKind = true;
        }

        var tileId = dto.TileId;
        if (tileId == null) { tileId = Guid.NewGuid().ToString(); state.NeedsSave = true; }

        var kindId = kind?.Id ?? TileKindIds.None;
        var leaf = new LeafTileNodeViewModel(kindId, null, _context.WorkingDirectory,
            _activationScope, _catalog, _context, _nameAllocator)
        {
            TileId = tileId,
            TileName = dto.TileName ?? _nameAllocator(kindId),
            LayoutChanged = scheduleSave
        };

        // After the tile id, not before: a terminal reads it through the context at launch, and a kind
        // built against a tile that had not been given its id yet would resolve ${tileId} to whatever
        // the constructor's default was.
        if (kind is not null)
            leaf.Content = kind.Create(_context with { TileId = () => leaf.TileId }, dto.Settings);

        _configureLeaf(leaf);
        if (dto.IsActive)
            activeLeaf = leaf;
        return leaf;
    }
}
