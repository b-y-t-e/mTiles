using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;

namespace MTerminal.ViewModels;

public partial class LeafTileNodeViewModel : TileNodeViewModel
{
    [ObservableProperty]
    private ObservableObject? _content;

    [ObservableProperty]
    private TileContentType _contentType;

    [ObservableProperty]
    private string _tileName = "";

    [ObservableProperty]
    private string _tileId = Guid.NewGuid().ToString();

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isChoosingProfile;

    partial void OnTileNameChanged(string value) => NotifyLayoutChanged();

    private readonly TileActivationScope _activationScope;
    private readonly Func<TileContentType, string, ObservableObject>? _contentFactory;
    private readonly Func<TileContentType, string>? _nameFactory;
    private readonly Func<IReadOnlyList<UserShellProfile>>? _profilesProvider;
    private readonly Func<UserShellProfile, string, ObservableObject>? _profileContentFactory;
    private readonly string _workingDirectory;

    public IReadOnlyList<UserShellProfile>? AvailableProfiles { get; private set; }

    public TileActivationScope ActivationScope => _activationScope;
    public Action<TileNodeViewModel>? RootReplaced { get; set; }
    public Action? RootCleared { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    public LeafTileNodeViewModel(TileContentType contentType, ObservableObject? content, string workingDirectory,
        TileActivationScope activationScope,
        Func<TileContentType, string, ObservableObject>? contentFactory = null,
        Func<TileContentType, string>? nameFactory = null,
        Func<IReadOnlyList<UserShellProfile>>? profilesProvider = null,
        Func<UserShellProfile, string, ObservableObject>? profileContentFactory = null)
    {
        _contentType = contentType;
        _content = content;
        _workingDirectory = workingDirectory;
        _activationScope = activationScope;
        _contentFactory = contentFactory;
        _nameFactory = nameFactory;
        _profilesProvider = profilesProvider;
        _profileContentFactory = profileContentFactory;
        _activationScope.ActiveTileChanged += OnActiveTileChanged;
    }

    public event Action? FocusRequested;

    public void Activate() => _activationScope.Activate(this);

    public void RequestFocus() => FocusRequested?.Invoke();

    private void OnActiveTileChanged(LeafTileNodeViewModel active) => IsActive = active == this;

    [RelayCommand]
    private void RestartTerminal()
    {
        if (Content is not TerminalTileViewModel tvm) return;
        if (tvm.CachedControl is not Iciclecreek.Terminal.TerminalControl tc) return;

        tvm.TileId = TileId;
        tc.Kill();

        if (tvm.StartupScript != null)
            Views.PtyWriter.AttachStartupScript(tc, tvm.StartupScript, TileId);

        _ = tc.LaunchProcess(_workingDirectory, tvm.Shell.ExecutablePath, tvm.Shell.Args);
    }

    [RelayCommand]
    private async Task ResetTileIdAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Generate new Tile ID and restart shell?"))
            return;

        TileId = Guid.NewGuid().ToString();
        NotifyLayoutChanged();
        RestartTerminal();
    }

    [RelayCommand]
    private void SplitHorizontal() => Split(Orientation.Horizontal);

    [RelayCommand]
    private void SplitVertical() => Split(Orientation.Vertical);

    [RelayCommand]
    private void SelectContentType(TileContentType type)
    {
        if (ContentType != TileContentType.Empty) return;

        if (type == TileContentType.Terminal)
        {
            var profiles = _profilesProvider?.Invoke();
            if (profiles != null && profiles.Count > 0)
            {
                AvailableProfiles = profiles;
                OnPropertyChanged(nameof(AvailableProfiles));
                IsChoosingProfile = true;
                return;
            }
        }

        CreateContentDirect(type);
    }

    [RelayCommand]
    private void SelectDefaultTerminal()
    {
        IsChoosingProfile = false;
        CreateContentDirect(TileContentType.Terminal);
    }

    [RelayCommand]
    private void SelectProfile(UserShellProfile profile)
    {
        IsChoosingProfile = false;
        var newContent = _profileContentFactory?.Invoke(profile, _workingDirectory);
        if (newContent == null) return;

        if (newContent is TerminalTileViewModel tvm)
            tvm.TileId = TileId;

        Content = newContent;
        ContentType = TileContentType.Terminal;
        TileName = _nameFactory?.Invoke(TileContentType.Terminal) ?? "Terminal";
        NotifyLayoutChanged();
    }

    [RelayCommand]
    private void CancelProfileSelection()
    {
        IsChoosingProfile = false;
    }

    private void CreateContentDirect(TileContentType type)
    {
        var newContent = _contentFactory?.Invoke(type, _workingDirectory);
        if (newContent == null) return;

        if (newContent is TerminalTileViewModel tvm)
            tvm.TileId = TileId;

        Content = newContent;
        ContentType = type;
        TileName = _nameFactory?.Invoke(type) ?? type.ToString();
        (newContent as IFileContent)?.RenameFile(TileName);
        NotifyLayoutChanged();
    }

    private void Split(Orientation orientation)
    {
        var newLeaf = new LeafTileNodeViewModel(TileContentType.Empty, null, _workingDirectory,
            _activationScope, _contentFactory, _nameFactory, _profilesProvider, _profileContentFactory)
        {
            TileName = "",
            LayoutChanged = LayoutChanged,
            RootReplaced = RootReplaced,
            RootCleared = RootCleared
        };

        var oldParent = Parent;

        var split = new SplitTileNodeViewModel(orientation, this, newLeaf)
        {
            Parent = oldParent,
            LayoutChanged = LayoutChanged
        };

        this.Parent = split;
        newLeaf.Parent = split;

        if (oldParent is SplitTileNodeViewModel parentSplit)
        {
            if (parentSplit.First == this)
                parentSplit.First = split;
            else
                parentSplit.Second = split;
        }
        else
        {
            RootReplaced?.Invoke(split);
        }

        NotifyLayoutChanged();
    }

    [RelayCommand]
    private void Close()
    {
        _activationScope.ActiveTileChanged -= OnActiveTileChanged;

        if (Content is IDisposable disposable)
            disposable.Dispose();

        if (Parent is not SplitTileNodeViewModel parentSplit)
        {
            RootCleared?.Invoke();
            return;
        }

        var sibling = (this == parentSplit.First) ? parentSplit.Second : parentSplit.First;
        if (sibling == null) { RootCleared?.Invoke(); return; }

        sibling.Parent = parentSplit.Parent;
        sibling.LayoutChanged = LayoutChanged;
        PropagateSiblingCallbacks(sibling);

        if (parentSplit.Parent is SplitTileNodeViewModel grandParent)
        {
            if (parentSplit == grandParent.First)
                grandParent.First = sibling;
            else
                grandParent.Second = sibling;
        }
        else
        {
            RootReplaced?.Invoke(sibling);
        }

        NotifyLayoutChanged();
    }

    private void PropagateSiblingCallbacks(TileNodeViewModel node)
    {
        if (node is LeafTileNodeViewModel leaf)
        {
            leaf.RootReplaced = RootReplaced;
            leaf.RootCleared = RootCleared;
            leaf.LayoutChanged = LayoutChanged;
        }
        else if (node is SplitTileNodeViewModel split)
        {
            split.LayoutChanged = LayoutChanged;
            if (split.First != null) PropagateSiblingCallbacks(split.First);
            if (split.Second != null) PropagateSiblingCallbacks(split.Second);
        }
    }
}
