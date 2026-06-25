using Avalonia.Layout;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services;

public sealed class TileTreeSerializer
{
    private readonly TileFactory _tileFactory;
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<ShellProfile> _availableShells;
    private readonly string _workingDirectory;
    private readonly Func<TileContentType, string> _nameAllocator;
    private readonly Action<LeafTileNodeViewModel> _configureLeaf;
    private readonly Func<IReadOnlyList<UserShellProfile>> _profilesProvider;
    private readonly TileActivationScope _activationScope;

    public TileTreeSerializer(
        TileFactory tileFactory,
        SettingsService settingsService,
        IReadOnlyList<ShellProfile> availableShells,
        string workingDirectory,
        Func<TileContentType, string> nameAllocator,
        Action<LeafTileNodeViewModel> configureLeaf,
        Func<IReadOnlyList<UserShellProfile>> profilesProvider,
        TileActivationScope activationScope)
    {
        _tileFactory = tileFactory;
        _settingsService = settingsService;
        _availableShells = availableShells;
        _workingDirectory = workingDirectory;
        _nameAllocator = nameAllocator;
        _configureLeaf = configureLeaf;
        _profilesProvider = profilesProvider;
        _activationScope = activationScope;
    }

    public TileNode? Serialize(TileNodeViewModel? vm)
    {
        return vm switch
        {
            LeafTileNodeViewModel leaf => new TileNode
            {
                IsLeaf = true,
                ContentType = leaf.ContentType,
                TileId = leaf.TileId,
                TileName = leaf.TileName,
                ShellName = (leaf.Content as TerminalTileViewModel)?.Shell.Name,
                UserProfileId = (leaf.Content as TerminalTileViewModel)?.UserProfileId,
                NoteFilePath = (leaf.Content as NoteTileViewModel)?.FilePath,
                TodoFilePath = (leaf.Content as TodoTileViewModel)?.FilePath,
                GoalFilePath = (leaf.Content as GoalTileViewModel)?.FilePath,
                IsActive = leaf.IsActive,
                Settings = TileFactory.SerializeSettings(leaf)
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

    public (TileNodeViewModel? Root, LeafTileNodeViewModel? ActiveLeaf) Deserialize(TileNode dto, Action scheduleSave)
    {
        LeafTileNodeViewModel? activeLeaf = null;
        bool migrated = false;
        var root = DeserializeNode(dto, scheduleSave, ref activeLeaf, ref migrated);
        if (migrated) scheduleSave();
        return (root, activeLeaf);
    }

    private TileNodeViewModel? DeserializeNode(TileNode dto, Action scheduleSave, ref LeafTileNodeViewModel? activeLeaf, ref bool migrated)
    {
        if (dto.IsLeaf)
        {
            var content = _tileFactory.CreateFromDto(dto, _workingDirectory, _availableShells, scheduleSave);

            var tileName = dto.TileName ?? _nameAllocator(dto.ContentType);
            var tileId = dto.TileId;
            if (tileId == null) { tileId = Guid.NewGuid().ToString(); migrated = true; }
            var leaf = new LeafTileNodeViewModel(dto.ContentType, content, _workingDirectory,
                _activationScope,
                (t, d) => _tileFactory.CreateContent(t, d), _nameAllocator,
                _profilesProvider,
                (profile, dir) => _tileFactory.CreateContent(TileContentType.Terminal, dir, profile))
            {
                TileId = tileId,
                TileName = tileName,
                LayoutChanged = scheduleSave
            };
            if (content is TerminalTileViewModel tvm)
                tvm.TileId = tileId;
            _configureLeaf(leaf);
            if (dto.IsActive)
                activeLeaf = leaf;
            return leaf;
        }

        var first = DeserializeNode(dto.First!, scheduleSave, ref activeLeaf, ref migrated);
        var second = DeserializeNode(dto.Second!, scheduleSave, ref activeLeaf, ref migrated);
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
}
