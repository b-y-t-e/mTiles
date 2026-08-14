using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using System.Linq;

namespace mTiles.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;
    private readonly TileFactory _tileFactory;
    private readonly TileTreeSerializer _serializer;
    private readonly Services.Speech.DictationService? _dictation;

    [ObservableProperty]
    private TileNodeViewModel? _rootTile;

    private LeafTileNodeViewModel? _lastActiveLeaf;

    public string WorkspaceId { get; }
    public string WorkingDirectory { get; }
    public ObservableCollection<ShellProfile> AvailableShells { get; } = [];

    private readonly TileActivationScope _activationScope = new();

    private HashSet<string>? _cachedAiToolBinaries;
    private DateTime _detectionCacheTime;
    private static readonly TimeSpan DetectionCacheTtl = TimeSpan.FromSeconds(30);

    private int _noteCount;
    private int _todoCount;
    private int _gitCount;
    private int _dbCount;
    private int _goalCount;
    private readonly HashSet<string> _usedTerminalNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TileNumberRegex = new(@"#(\d+)$", RegexOptions.Compiled);

    public WorkspaceViewModel(Workspace workspace, PersistenceService persistenceService, SettingsService settingsService,
        DatabaseServiceManager? dbManager = null, Services.Speech.DictationService? dictation = null)
    {
        WorkspaceId = workspace.Id;
        WorkingDirectory = workspace.DirectoryPath;
        _persistenceService = persistenceService;
        _settingsService = settingsService;
        _dictation = dictation;
        _tileFactory = new TileFactory(settingsService, ScheduleSave, dbManager);

        foreach (var shell in ShellDetector.Detect())
            AvailableShells.Add(shell);

        _serializer = new TileTreeSerializer(
            _tileFactory,
            _settingsService,
            AvailableShells,
            WorkingDirectory,
            AllocateTileName,
            ConfigureLeafCallbacks,
            GetAvailableProfiles,
            _activationScope);

        var state = persistenceService.LoadLayout(workspace.Id);
        if (state?.RootTile != null)
        {
            InitCountersFromDto(state.RootTile);
            var (root, activeLeaf) = _serializer.Deserialize(state.RootTile, ScheduleSave);
            RootTile = root;
            _lastActiveLeaf = activeLeaf;
        }
        else
        {
            RootTile = CreateLeaf(TileContentType.Empty, null, "");
        }
    }

    private LeafTileNodeViewModel CreateLeaf(TileContentType type, ObservableObject? content, string tileName)
    {
        var leaf = new LeafTileNodeViewModel(type, content, WorkingDirectory,
            _activationScope,
            (t, d) => _tileFactory.CreateContent(t, d),
            AllocateTileName,
            GetAvailableProfiles,
            (profile, dir) => _tileFactory.CreateContent(TileContentType.Terminal, dir, profile))
        {
            TileName = tileName
        };
        // Everything else a tile needs is decided in one place, including LayoutChanged. Setting it here
        // as well is the arrangement the whole fix was about removing: two lists to keep in step.
        ConfigureLeafCallbacks(leaf);
        return leaf;
    }

    private IReadOnlyList<UserShellProfile> GetAvailableProfiles()
    {
        var profiles = _settingsService.Settings.ShellProfiles;
        if (profiles.Count == 0) return profiles;

        if (!profiles.Any(p => !string.IsNullOrEmpty(p.RequiredAiToolBinaryName)))
            return profiles;

        var now = DateTime.UtcNow;
        if (now - _detectionCacheTime > DetectionCacheTtl)
        {
            _detectionCacheTime = now;
            _cachedAiToolBinaries = null;
        }

        _cachedAiToolBinaries ??= new HashSet<string>(
            AiToolDetector.Detect(
                _settingsService.Settings.CustomAiToolPaths,
                _settingsService.Settings.CustomAiTools)
            .Where(t => t.IsInstalled).Select(t => t.BinaryName),
            StringComparer.OrdinalIgnoreCase);

        return profiles.Where(p =>
        {
            if (!string.IsNullOrEmpty(p.RequiredAiToolBinaryName))
                return _cachedAiToolBinaries.Contains(p.RequiredAiToolBinaryName);
            return true;
        }).ToList();
    }

    public TileActivationScope ActivationScope => _activationScope;

    /// <summary>
    /// The tile a shortcut should act on: the one the user last worked in, and <b>nothing</b> if that
    /// tile is no longer in the tree.
    /// </summary>
    /// <remarks>
    /// <para>Only if it is still in the tree. After a tile is closed or the layout is rebuilt,
    /// <c>_lastActiveLeaf</c> can point at a detached leaf whose content has been disposed — dictating
    /// into that sends the words to a terminal nobody can see.</para>
    /// <para>And no falling back to "whatever tile is first". That is right for
    /// <see cref="FocusActiveTile"/>, where focus has to land somewhere, and wrong here: the first leaf
    /// is an arbitrary tile the user is not looking at, and with <c>AutoSubmitEnter</c> on, delivering
    /// there does not paste a sentence — it <em>runs a command</em> in a terminal nobody chose. Null
    /// instead: the sink then tries whatever text control has the keyboard, and failing that the service
    /// reports the transcript as undeliverable and quotes it back.</para>
    /// </remarks>
    public LeafTileNodeViewModel? ActiveTile => ResolveActiveTile(orAnyTile: false);

    private LeafTileNodeViewModel? ResolveActiveTile(bool orAnyTile)
    {
        var target = _lastActiveLeaf;
        if (target != null && EnumerateLeaves(RootTile).Contains(target))
            return target;

        return orAnyTile ? EnumerateLeaves(RootTile).FirstOrDefault() : null;
    }

    public void ActivateLastTile()
    {
        _lastActiveLeaf?.Activate();
    }

    /// <summary>Puts the keyboard back in this workspace — any tile will do, and one has to.</summary>
    public void FocusActiveTile() => ResolveActiveTile(orAnyTile: true)?.RequestFocus();

    private static IEnumerable<LeafTileNodeViewModel> EnumerateLeaves(TileNodeViewModel? node)
    {
        switch (node)
        {
            case LeafTileNodeViewModel leaf:
                yield return leaf;
                break;
            case SplitTileNodeViewModel split:
                foreach (var l in EnumerateLeaves(split.First)) yield return l;
                foreach (var l in EnumerateLeaves(split.Second)) yield return l;
                break;
        }
    }

    private void ConfigureLeafCallbacks(LeafTileNodeViewModel leaf)
    {
        // Including this one. It was the single callback Split still copied by hand, which is exactly the
        // arrangement that let a new callback be added here and forgotten there.
        leaf.LayoutChanged = ScheduleSave;
        leaf.Dictation = _dictation;
        // Passed on to every tile, so a tile created by splitting is configured by this same method
        // rather than by whatever its parent remembered to copy.
        leaf.ConfigureNewLeaf = ConfigureLeafCallbacks;
        leaf.RootReplaced = newRoot => RootTile = ConfigureRoot(newRoot);
        leaf.RootCleared = () => { RootTile = CreateLeaf(TileContentType.Empty, null, ""); ScheduleSave(); };
        leaf.PropertyChanged -= OnLeafPropertyChanged;
        leaf.PropertyChanged += OnLeafPropertyChanged;
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeafTileNodeViewModel.IsActive)
            && sender is LeafTileNodeViewModel leaf && leaf.IsActive)
            _lastActiveLeaf = leaf;
    }

    private TileNodeViewModel ConfigureRoot(TileNodeViewModel node)
    {
        node.LayoutChanged = ScheduleSave;
        PropagateCallbacks(node);
        ScheduleSave();
        return node;
    }

    private void PropagateCallbacks(TileNodeViewModel node)
    {
        node.LayoutChanged = ScheduleSave;
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

    private string AllocateTileName(TileContentType type)
    {
        if (type == TileContentType.Terminal)
        {
            var name = TileNameGenerator.Generate(_usedTerminalNames);
            _usedTerminalNames.Add(name);
            return name;
        }
        return TileFactory.AllocateTileName(type, ref _noteCount, ref _todoCount, ref _gitCount, ref _dbCount, ref _goalCount);
    }

    private void InitCountersFromDto(TileNode? node)
    {
        if (node == null) return;
        if (node.IsLeaf)
        {
            if (node.TileName != null)
            {
                if (node.ContentType == TileContentType.Terminal)
                {
                    _usedTerminalNames.Add(node.TileName);
                }
                else
                {
                    var match = TileNumberRegex.Match(node.TileName);
                    if (match.Success)
                    {
                        var num = int.Parse(match.Groups[1].Value);
                        if (node.ContentType == TileContentType.Note)
                            _noteCount = Math.Max(_noteCount, num);
                        else if (node.ContentType == TileContentType.Todo)
                            _todoCount = Math.Max(_todoCount, num);
                        else if (node.ContentType == TileContentType.Git)
                            _gitCount = Math.Max(_gitCount, num);
                        else if (node.ContentType == TileContentType.Database)
                            _dbCount = Math.Max(_dbCount, num);
                        else if (node.ContentType == TileContentType.Goal)
                            _goalCount = Math.Max(_goalCount, num);
                    }
                }
            }
        }
        else
        {
            InitCountersFromDto(node.First);
            InitCountersFromDto(node.Second);
        }
    }

    private void ScheduleSave()
    {
        _persistenceService.DebouncedSaveLayout(WorkspaceId, () => _serializer.Serialize(RootTile));
    }

    public void Dispose()
    {
        DisposeTree(RootTile);
    }

    private void DisposeTree(TileNodeViewModel? node)
    {
        if (node is LeafTileNodeViewModel leaf)
        {
            leaf.PropertyChanged -= OnLeafPropertyChanged;
            // The tile itself, which takes its content with it: it is subscribed to services that
            // outlive this workspace, and those subscriptions are what would keep the whole tile alive.
            leaf.Dispose();
        }
        else if (node is SplitTileNodeViewModel split)
        {
            DisposeTree(split.First);
            DisposeTree(split.Second);
        }
    }
}
