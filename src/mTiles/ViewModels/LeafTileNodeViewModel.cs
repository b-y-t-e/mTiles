using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

public partial class LeafTileNodeViewModel : TileNodeViewModel, IDisposable
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

    public bool HasProfile => Content is TerminalTileViewModel { UserProfileId: not null };

    /// <summary>Whether this tile is working right now — false for content that has no notion of it,
    /// and false once the tile has been disposed of.</summary>
    /// <remarks>A closed tile is not working, whatever its content was doing a moment ago: closing one
    /// takes it out of the tree without the workspace's own root changing, so the light it leaves lit is
    /// one nothing else would ever put out.</remarks>
    public bool IsBusy => !_disposed && (Content as IBusyTile)?.IsBusy == true;

    partial void OnContentChanged(ObservableObject? oldValue, ObservableObject? newValue)
    {
        WatchContentBusy(oldValue, newValue);
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsBusy));
    }

    /// <summary>Follows the busy state of whatever content the tile holds now.</summary>
    /// <remarks>The tile watches its own content and the workspace watches its tiles, so neither has to
    /// walk the other's tree — and content that is not an <see cref="IBusyTile"/> is not subscribed to
    /// at all.</remarks>
    private void WatchContentBusy(ObservableObject? oldContent, ObservableObject? newContent)
    {
        if (oldContent is IBusyTile previous)
            previous.PropertyChanged -= OnContentPropertyChanged;
        if (newContent is IBusyTile current)
            current.PropertyChanged += OnContentPropertyChanged;
    }

    private void OnContentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IBusyTile.IsBusy))
            OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnContentTypeChanged(TileContentType value) => OnPropertyChanged(nameof(CanDictate));
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

    /// <summary>
    /// Hands a newly created tile to whoever knows what a tile needs — the workspace.
    /// </summary>
    /// <remarks>
    /// <see cref="Split"/> used to copy the callbacks it knew about by hand, which meant a new tile got
    /// exactly the ones somebody remembered to add to that list. It only worked at all because splitting
    /// the <em>root</em> rebuilds the whole tree through the workspace; splitting anything else took the
    /// other branch and configured nothing. So from the second split onwards a new tile had no dictation
    /// service (no microphone button, ever) and was invisible to the active-tile tracking the shortcut
    /// aims at. One callback into the one place that knows, rather than a list to keep in step.
    /// </remarks>
    public Action<LeafTileNodeViewModel>? ConfigureNewLeaf { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    public LeafTileNodeViewModel(TileContentType contentType, ObservableObject? content, string workingDirectory,
        TileActivationScope activationScope,
        Func<TileContentType, string, ObservableObject>? contentFactory = null,
        Func<TileContentType, string>? nameFactory = null,
        Func<IReadOnlyList<UserShellProfile>>? profilesProvider = null,
        Func<UserShellProfile, string, ObservableObject>? profileContentFactory = null)
    {
        _contentType = contentType;
        // Assigned to the backing field, so the generated OnContentChanged never runs: a tile built
        // from a saved layout arrives with its content already in hand, and without this it would
        // never follow it. Every other route in goes through the property and is covered there.
        _content = content;
        WatchContentBusy(null, content);
        _workingDirectory = workingDirectory;
        _activationScope = activationScope;
        _contentFactory = contentFactory;
        _nameFactory = nameFactory;
        _profilesProvider = profilesProvider;
        _profileContentFactory = profileContentFactory;
        _activationScope.ActiveTileChanged += OnActiveTileChanged;
    }

    private DictationService? _dictation;

    /// <summary>
    /// The application's one dictation service, handed to every tile so each can offer the microphone
    /// and show whether it is the tile currently being spoken into. Null when dictation was never wired
    /// up — the tests build tiles without it.
    /// </summary>
    public DictationService? Dictation
    {
        get => _dictation;
        set
        {
            if (ReferenceEquals(_dictation, value))
                return;

            if (_dictation is not null)
                _dictation.StateChanged -= OnDictationChanged;

            _dictation = value;
            if (_dictation is not null)
                _dictation.StateChanged += OnDictationChanged;

            OnDictationChanged();
        }
    }

    /// <summary>True while this tile is the one being dictated into, recording or transcribing.</summary>
    [ObservableProperty]
    private bool _isDictating;

    /// <summary>
    /// Whether the "this tile is active" strip should be lit.
    /// </summary>
    /// <remarks>
    /// Not while dictating. The strip and the dictation border say overlapping things — the border
    /// frames this tile, so it already answers "which one" — and two markers competing at the top edge
    /// of the same tile is noise rather than information. The outline comes back the moment the
    /// transcript lands, which is also the moment it starts meaning something again.
    /// </remarks>
    public bool ShowsActiveOutline => IsActive && !IsDictating;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ShowsActiveOutline));
    partial void OnIsDictatingChanged(bool value) => OnPropertyChanged(nameof(ShowsActiveOutline));

    /// <summary>
    /// The two halves of that, kept apart because they are shown differently: the tile's border breathes
    /// slowly while the microphone is open and pulses quickly while the words are being worked out.
    /// </summary>
    /// <remarks>
    /// One flag with a phase enum would do as well, but two booleans are what a style selector can bind
    /// to without a converter, and the view already reacts to property names.
    /// </remarks>
    [ObservableProperty]
    private bool _isRecordingDictation;

    [ObservableProperty]
    private bool _isTranscribingDictation;

    /// <summary>Whether to offer the microphone at all: dictation switched on, and a terminal to type
    /// into. The other tile kinds take dictation through the shortcut, into whatever text box has the
    /// keyboard — a button in their header would have nowhere to put the words.</summary>
    public bool CanDictate =>
        ContentType == TileContentType.Terminal && Dictation is { Speech.Enabled: true };

    private void OnDictationChanged()
    {
        var mine = _dictation is { State: not DictationState.Idle } d && ReferenceEquals(d.Owner, this);

        // The umbrella state first, so no observer sees "recording" alongside "not dictating". Nothing
        // depends on the order any more — ShowsActiveOutline raises its own notification either way — but
        // a moment of self-contradiction is not something to leave lying around for the next reader.
        IsDictating = mine;
        IsRecordingDictation = mine && _dictation!.State == DictationState.Recording;
        IsTranscribingDictation = mine && _dictation!.State == DictationState.Transcribing;
        OnPropertyChanged(nameof(CanDictate));
    }

    /// <summary>
    /// Starts dictation for this tile, or ends the recording this tile started.
    /// </summary>
    /// <remarks>
    /// It never takes the microphone off another tile — one microphone, one destination, and taking it
    /// over mid-sentence would put half an utterance somewhere the user was not looking. But it does not
    /// stay <em>silent</em> about that either: the request falls through to the service, which refuses
    /// it and says which kind of busy it is. A button that answers a click with nothing at all is
    /// indistinguishable from a broken one.
    /// </remarks>
    [RelayCommand]
    private void ToggleDictation()
    {
        if (Dictation is not { } dictation)
            return;

        // Only a *recording* is stopped by the button. Clicking it while this tile's own transcript is
        // still being worked out used to call Stop, which returns early when nothing is recording — so
        // the button answered with nothing at all. Falling through to Start instead gets the request
        // refused with a reason ("still working on the previous recording"), which is what a click asked.
        if (dictation.State == DictationState.Recording && ReferenceEquals(dictation.Owner, this))
        {
            dictation.Stop();
            return;
        }

        var speech = dictation.Speech;
        dictation.Start(this, text => DictationTextSink.Insert(this, text, speech));
    }

    /// <summary>
    /// Ends the tile: the subscriptions that hold it alive, a recording it started, and its content.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Dictation"/> is the subscription that matters: the service lives as long as the
    /// application, so a tile still listening to its <c>StateChanged</c> is a tile the garbage collector
    /// can never take — along with its content, its terminal and everything they hold. Closing one tile
    /// went through <see cref="CloseAsync"/> and was fine; closing a whole <em>workspace</em> did not,
    /// and leaked every tile in it.</para>
    /// <para>The content goes with it, and that is deliberate. Disposing a tile and disposing what is
    /// inside it were two calls every teardown had to remember to make — closing one tile made both,
    /// closing a workspace made only the second — which is the same shape of bug as the one above, one
    /// level down. One call now, and the pair cannot come apart again.</para>
    /// <para>Idempotent, because both of those callers still exist: a tile closed by its own command can
    /// be torn down with its workspace a moment later.</para>
    /// </remarks>
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _activationScope.ActiveTileChanged -= OnActiveTileChanged;

        // A tile that is recording as it goes would deliver its transcript to a terminal that is about
        // to be disposed of. Asking the service who owns the recording, not this tile's own IsDictating:
        // that flag is set from a dispatcher callback, so between starting and the callback running it
        // still reads false — and a tile closed in that window would leave its recording going.
        if (Dictation is { } dictation && ReferenceEquals(dictation.Owner, this))
            dictation.Cancel();

        Dictation = null;

        WatchContentBusy(Content, null);
        // Said out loud, because the content is no longer there to say it: the workspace is still
        // listening to this leaf, and this is the last moment at which it hears anything from it.
        OnPropertyChanged(nameof(IsBusy));

        if (Content is IDisposable disposable)
            disposable.Dispose();
    }

    public event Action? FocusRequested;

    public void Activate() => _activationScope.Activate(this);

    public void RequestFocus() => FocusRequested?.Invoke();

    private void OnActiveTileChanged(LeafTileNodeViewModel active) => IsActive = active == this;

    [RelayCommand]
    private async Task RestartTerminalAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Restart shell?"))
            return;

        DoRestartTerminal();
    }

    [RelayCommand]
    private async Task ResetTileIdAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Generate new Tile ID and restart shell?"))
            return;

        TileId = Guid.NewGuid().ToString();
        NotifyLayoutChanged();
        DoRestartTerminal();
    }

    private void DoRestartTerminal()
    {
        if (Content is not TerminalTileViewModel tvm) return;
        if (tvm.CachedControl is not Terminal.Avalonia.TerminalControl tc) return;

        // No Kill() first. Not because it would stall — the restart kills the child itself, and that
        // call blocks the UI thread for as long as the child takes either way — but because killing
        // here races the restart: it would leave the launcher waiting on a session that had already
        // gone, and the previous chain seeing an exit it is entitled to relaunch. Sequencing the kill,
        // the wait and the start is precisely what RestartAsync exists for, and it serialises
        // overlapping restarts on top.
        tvm.TileId = TileId;
        Services.TileLauncher.Launch(tc, tvm);
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
            TileName = ""
        };

        // Everything a tile needs comes from the workspace, including the services it must be subscribed
        // to. Splitting the root happens to reconfigure the whole tree afterwards; splitting anything
        // else does not, and this is what makes the two paths equal.
        if (ConfigureNewLeaf is { } configure)
        {
            configure(newLeaf);
        }
        else
        {
            // Nobody configured *this* tile either — a tile built by hand, which in practice means a
            // test. Inheritance is all there is to go on, and a new tile with no LayoutChanged never
            // saves the layout it just changed. ConfigureNewLeaf is not copied because it is null: that
            // is the condition of being in this branch.
            newLeaf.LayoutChanged = LayoutChanged;
            newLeaf.RootReplaced = RootReplaced;
            newLeaf.RootCleared = RootCleared;
            newLeaf.Dictation = Dictation;
        }

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
    private async Task CloseAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Close tile?"))
            return;

        Dispose();          // the content goes with it

        if (!Views.TileDragDrop.DetachFromTree(this))
        {
            RootCleared?.Invoke();
            return;
        }

        NotifyLayoutChanged();
    }
}
