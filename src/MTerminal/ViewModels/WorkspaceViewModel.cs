using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;
using MTerminal.Services;
using System.Linq;

namespace MTerminal.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;
    private readonly TileFactory _tileFactory;
    private readonly TileTreeSerializer _serializer;

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

    private int _terminalCount;
    private int _noteCount;
    private int _todoCount;
    private int _gitCount;

    private static readonly Regex TileNumberRegex = new(@"#(\d+)$", RegexOptions.Compiled);

    public WorkspaceViewModel(Workspace workspace, PersistenceService persistenceService, SettingsService settingsService)
    {
        WorkspaceId = workspace.Id;
        WorkingDirectory = workspace.DirectoryPath;
        _persistenceService = persistenceService;
        _settingsService = settingsService;
        _tileFactory = new TileFactory(settingsService, ScheduleSave);

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
            TileName = tileName,
            LayoutChanged = ScheduleSave
        };
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

    public void ActivateLastTile()
    {
        _lastActiveLeaf?.Activate();
    }

    public void FocusActiveTile()
    {
        _lastActiveLeaf?.RequestFocus();
    }

    private void ConfigureLeafCallbacks(LeafTileNodeViewModel leaf)
    {
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

    private string AllocateTileName(TileContentType type) =>
        TileFactory.AllocateTileName(type, ref _terminalCount, ref _noteCount, ref _todoCount, ref _gitCount);

    private void InitCountersFromDto(TileNode? node)
    {
        if (node == null) return;
        if (node.IsLeaf)
        {
            if (node.TileName != null)
            {
                var match = TileNumberRegex.Match(node.TileName);
                if (match.Success)
                {
                    var num = int.Parse(match.Groups[1].Value);
                    if (node.ContentType == TileContentType.Terminal)
                        _terminalCount = Math.Max(_terminalCount, num);
                    else if (node.ContentType == TileContentType.Note)
                        _noteCount = Math.Max(_noteCount, num);
                    else if (node.ContentType == TileContentType.Todo)
                        _todoCount = Math.Max(_todoCount, num);
                    else if (node.ContentType == TileContentType.Git)
                        _gitCount = Math.Max(_gitCount, num);
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
            if (leaf.Content is IDisposable d)
                d.Dispose();
        }
        else if (node is SplitTileNodeViewModel split)
        {
            DisposeTree(split.First);
            DisposeTree(split.Second);
        }
    }
}
