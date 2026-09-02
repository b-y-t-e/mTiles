using System.Text.Json.Nodes;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services.Speech;
using mTiles.Services.Tiles;

namespace mTiles.ViewModels;

public partial class LeafTileNodeViewModel : TileNodeViewModel, IDisposable
{
    [ObservableProperty]
    private ITile? _content;

    /// <summary>Which kind of tile this is, or empty for one that has not been given content yet.</summary>
    /// <remarks>Stored on the tile rather than read from <see cref="Content"/> because an empty tile has
    /// no content to ask, and "empty" is exactly the state the chooser is drawn for.</remarks>
    [ObservableProperty]
    private string _kindId = TileKindIds.None;

    [ObservableProperty]
    private string _tileName = "";

    [ObservableProperty]
    private string _tileId = Guid.NewGuid().ToString();

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Whether the tile is showing a kind's own step instead of the list of kinds.</summary>
    [ObservableProperty]
    private bool _isChoosingSetup;

    /// <summary>Whether "New session" means anything here.</summary>
    /// <remarks>
    /// It generates a fresh <see cref="TileId"/> and restarts, and the id is only ever <em>used</em> as
    /// an agent's conversation — so on a tile running a plain shell the command would restart the shell
    /// and claim to have done something else. The one place left where this class knows what its content
    /// is, and it is about the tile's own identity rather than about anything the content can do.
    /// </remarks>
    public bool HasSession => Content is AgentTileViewModel;

    /// <summary>The tile's content when it is an agent, for the two questions only an agent answers.
    /// </summary>
    /// <remarks>The exception <see cref="HasSession"/> already admits to: the header's menu binds to
    /// this class rather than to the content, and "Run as" is wanted by exactly one kind. An interface
    /// asked of the content by capability would be the rule rather than the exception — and a whole
    /// interface for one implementer, which <c>docs/TILES.md</c> says has to be earned. Promote it if a
    /// second kind ever wants the same thing.</remarks>
    private AgentTileViewModel? Agent => _disposed ? null : Content as AgentTileViewModel;

    /// <summary>The instances the header's "Run as" submenu is offering right now.</summary>
    public IReadOnlyList<AgentInstanceChoice> AgentInstances { get; private set; } = [];

    /// <summary>Whether that submenu has a choice to offer.</summary>
    /// <remarks>Asked of the entries rather than of their number: the instance the tile is running is
    /// usually one of them, but not when it has been substituted or is no longer available — and those
    /// are exactly the tiles switching exists to rescue, so counting would hide the menu on a machine
    /// with one alternative in the one case that needs it. A menu whose only item is a tick on what is
    /// already true is a click that cannot do anything.</remarks>
    public bool CanSwitchAgentInstance => AgentInstances.Any(choice => !choice.IsCurrent);

    /// <summary>
    /// Rebuilds the list of instances, which is what opening the menu is for.
    /// </summary>
    /// <remarks>Built when the menu opens rather than followed as a live collection: instances are
    /// added, renamed and deleted in Settings while the tile lives, and a subscription per agent tile to
    /// <c>SettingsChanged</c> buys nothing a menu about to be drawn does not get for free.</remarks>
    public void RefreshAgentInstances()
    {
        AgentInstances = Agent is { } agent
            ?
            [
                .. agent.SwitchTargets.Select(instance => new AgentInstanceChoice(
                    instance.Name, instance.Id == agent.InstanceId,
                    () => SwitchAgentInstanceAsync(instance.Id)))
            ]
            : [];

        OnPropertyChanged(nameof(AgentInstances));
        OnPropertyChanged(nameof(CanSwitchAgentInstance));
    }

    /// <summary>
    /// Runs this tile as another configured instance of the same agent.
    /// </summary>
    /// <remarks>
    /// <para>Destructive — it stops whatever the shell is running — so it asks first, and an unwired
    /// <see cref="ConfirmAction"/> answers <b>no</b>. Not a <c>TileAction</c> for the same reason
    /// Restart shell is not one: that list goes to a paired phone, which cannot be shown what is about
    /// to die.</para>
    /// <para>The instance and the conversation are settled first and the tile is restarted afterwards,
    /// because the launch reads both — <c>PrepareForLaunchAsync</c> resolves the model against the new
    /// account.</para>
    /// </remarks>
    private async Task SwitchAgentInstanceAsync(string instanceId)
    {
        if (Agent is not { } agent) return;
        if (agent.ConfirmationForSwitchTo(instanceId) is not { } question) return;
        if (ConfirmAction is null || !await ConfirmAction(question)) return;

        agent.SwitchTo(instanceId);
        await InvokeActionAsync(TileActionIds.Restart);
    }

    /// <summary>What the tile's own header and a paired phone may ask its content to do.</summary>
    /// <remarks>Empty for content that offers nothing, so no caller has to ask whether there is a list
    /// before reading one.</remarks>
    public IReadOnlyList<TileAction> Actions =>
        _disposed ? [] : (Content as ITileActions)?.Actions ?? [];

    /// <summary>Whether the header's Restart button and Ctrl+Shift+R have anything to do here.</summary>
    /// <remarks>Asked of the content's own list rather than of what kind of tile this is: a second kind
    /// that runs something restartable gets the button by offering the action, and this class does not
    /// have to learn about it.</remarks>
    public bool CanRestart => Actions.Any(a => a.Id == TileActionIds.Restart);

    /// <summary>Whether this tile is working right now — false for content that has no notion of it,
    /// and false once the tile has been disposed of.</summary>
    /// <remarks>A closed tile is not working, whatever its content was doing a moment ago: closing one
    /// takes it out of the tree without the workspace's own root changing, so the light it leaves lit is
    /// one nothing else would ever put out.</remarks>
    public bool IsBusy => !_disposed && (Content as IBusyTile)?.IsBusy == true;

    /// <summary>The process this tile started, when its content runs one and is still open.</summary>
    /// <remarks>Asked of the content rather than of its type, and null for content that runs nothing —
    /// a note holds no process and is never asked. Guarded by <c>_disposed</c> for the same reason
    /// <see cref="IsBusy"/> is: a closed tile's shell is being killed, and its memory is not the
    /// workspace's any more.</remarks>
    public int? ChildProcessId => _disposed ? null : (Content as IProcessTile)?.ChildProcessId;

    /// <summary>Whether giving this tile the whole workspace would show anything more
    /// (<see cref="IMaximizableTile"/>), and whether there is a workspace to give.</summary>
    /// <remarks><para>Asked of the content rather than of the kind, for the reason
    /// <see cref="CanRestart"/> is: a kind added later gets the button by implementing the capability,
    /// and this class learns nothing about it. The scope is null in a tile built by hand — a test —
    /// where there is no workspace to fill.</para>
    /// <para>And there has to be a split above it. A tile that is the whole tree — the one a first run
    /// opens with — already fills the workspace, so there is nothing for the gesture to do: without this
    /// the press changed nothing on screen while the button lit up, the glyph turned into
    /// <c>FullscreenExit</c> and the splits stood down, offering the user a way out of something they
    /// had never gone into.</para></remarks>
    public bool CanMaximize =>
        !_disposed && Content is IMaximizableTile && MaximizeScope is not null
        && Parent is SplitTileNodeViewModel;

    /// <summary>True while this tile is the one filling the workspace.</summary>
    /// <remarks>Written by <see cref="TileMaximizeScope"/> and by nothing else: the scope is what knows
    /// that only one tile can be maximized at a time, and a tile setting its own flag would be a second
    /// tile lit up beside the one actually on screen.</remarks>
    [ObservableProperty]
    private bool _isMaximized;

    /// <summary>The workspace's answer to "which tile has the whole of it", handed to every tile the
    /// same way its dictation service is.</summary>
    public TileMaximizeScope? MaximizeScope { get; set; }

    /// <summary>
    /// Gives this tile the whole workspace, or hands it back.
    /// </summary>
    /// <remarks>One command for both directions, because it is one button: the header shows which way
    /// it will go by the glyph on it, and a tile that filled the screen with no way back on the same
    /// button is the thing a maximize gesture is most often wrong about.</remarks>
    [RelayCommand]
    private void ToggleMaximize()
    {
        if (!CanMaximize) return;
        MaximizeScope!.Toggle(this);

        // Both ways through, the tile's view is detached from the visual tree and put back — as the
        // split's only content on the way in, into the grid on the way out — and Avalonia drops the
        // keyboard focus with it. Without this the terminal that just filled the screen stopped taking
        // what was typed, and Ctrl+Shift+F never reached the header that would have brought the layout
        // back, so the only way out was the mouse.
        Activate();
        RequestFocus();
    }

    /// <summary>Keeps what the tile can do in step with where it hangs in the tree.</summary>
    /// <remarks>A tile is moved between parents by a close, a split and a drop, and none of those goes
    /// anywhere near this class — but a leaf lifted into the root's own slot stops having a full screen
    /// to go to, and the header is the last thing that would find out.</remarks>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Parent))
            OnPropertyChanged(nameof(CanMaximize));
    }

    partial void OnContentChanged(ITile? oldValue, ITile? newValue)
    {
        WatchContent(oldValue, newValue);
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CanMaximize));
        RefreshAgentInstances();
        OnPropertyChanged(nameof(IsBusy));
        RaiseActionsChanged();
    }

    /// <summary>Follows whatever content the tile holds now.</summary>
    /// <remarks>The tile watches its own content and the workspace watches its tiles, so neither has to
    /// walk the other's tree.</remarks>
    private void WatchContent(ITile? oldContent, ITile? newContent)
    {
        if (oldContent is not null)
            oldContent.PropertyChanged -= OnContentPropertyChanged;
        if (newContent is not null)
            newContent.PropertyChanged += OnContentPropertyChanged;
    }

    private void OnContentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IBusyTile.IsBusy))
            OnPropertyChanged(nameof(IsBusy));

        // Anything at all, and deliberately: what an action's IsEnabled is computed from is the
        // content's business — a git tile's loading flag, a goal's phase — and a list of the properties
        // each of them happens to use would be a copy of six classes' internals kept here, out of date
        // from the first one that changed. Recomputing a list of three records is nothing.
        RaiseActionsChanged();
    }

    private void RaiseActionsChanged()
    {
        OnPropertyChanged(nameof(Actions));
        OnPropertyChanged(nameof(CanRestart));
    }

    partial void OnKindIdChanged(string value) => OnPropertyChanged(nameof(CanDictate));
    partial void OnTileNameChanged(string value) => NotifyLayoutChanged();

    private readonly TileActivationScope _activationScope;
    private readonly TileCatalog? _catalog;
    private readonly TileContext? _context;
    private readonly Func<string, string>? _nameFactory;
    private readonly string _workingDirectory;

    /// <summary>The cards the step on screen is offering, and the kind that asked for them.</summary>
    public IReadOnlyList<TileSetupOption> SetupOptions { get; private set; } = [];

    private string _setupKindId = TileKindIds.None;

    /// <summary>Every kind a tile can be given, in the order the chooser offers them.</summary>
    public IReadOnlyList<ITileKind> AvailableKinds =>
        _catalog?.Entries.Select(e => e.Kind).ToList() ?? [];

    /// <summary>The kind this tile is, or null while it is empty — what the header draws its glyph
    /// from.</summary>
    public ITileKind? Kind => _catalog?.Kind(KindId);

    /// <summary>The registry itself, for the one thing only the view can do with it: build the control
    /// that draws this tile's content.</summary>
    public TileCatalog? Catalog => _catalog;

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

    /// <param name="catalog">Every kind this tile could be given. Null in a test that only ever builds
    /// tiles by hand.</param>
    /// <param name="context">What a kind needs in order to build content here. The tile's own identity
    /// is added to it, because the id is this object's and moves under whatever it holds.</param>
    public LeafTileNodeViewModel(string kindId, ITile? content, string workingDirectory,
        TileActivationScope activationScope,
        TileCatalog? catalog = null,
        TileContext? context = null,
        Func<string, string>? nameFactory = null)
    {
        _kindId = kindId;
        // Assigned to the backing field, so the generated OnContentChanged never runs: a tile built
        // from a saved layout arrives with its content already in hand, and without this it would
        // never follow it. Every other route in goes through the property and is covered there.
        _content = content;
        WatchContent(null, content);
        _workingDirectory = workingDirectory;
        _activationScope = activationScope;
        _catalog = catalog;
        _context = context is null ? null : context with { TileId = () => TileId };
        _nameFactory = nameFactory;
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

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsActiveOutline));

        // Only on the way in, and only for content that asked: see IActivatableTile. Guarded on
        // _disposed like every other reach into Content here — Dispose leaves the reference in place.
        if (value && !_disposed && Content is IActivatableTile activatable)
            activatable.OnActivated();
    }
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

    /// <summary>Whether to offer the microphone at all: dictation switched on, and content with
    /// somewhere to put the words. The other tile kinds take dictation through the shortcut, into
    /// whatever text box has the keyboard — a button in their header would have nowhere to type.</summary>
    public bool CanDictate => Content is ITextInputTile && Dictation is { Speech.Enabled: true };

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
    /// level down. One call now, and the pair cannot come apart again. It no longer asks whether the
    /// content happens to be disposable either: every <see cref="ITile"/> is.</para>
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

        // Before anything else: a maximized tile being closed leaves the splits above it soloed on a
        // child that is about to be taken out of the tree, and the workspace would come back showing
        // one branch of itself with nothing able to put the rest back.
        MaximizeScope?.Forget(this);
        OnPropertyChanged(nameof(CanMaximize));

        var content = Content;
        WatchContent(content, null);
        // Said out loud, because the content is no longer there to say it: the workspace is still
        // listening to this leaf, and this is the last moment at which it hears anything from it.
        OnPropertyChanged(nameof(IsBusy));
        RaiseActionsChanged();

        content?.Dispose();
    }

    public event Action? FocusRequested;

    public void Activate() => _activationScope.Activate(this);

    public void RequestFocus() => FocusRequested?.Invoke();

    private void OnActiveTileChanged(LeafTileNodeViewModel active) => IsActive = active == this;

    /// <summary>Does one of the things this tile's content offers, or says why it did not.</summary>
    /// <remarks>The single route in, so the header, a keyboard shortcut and a paired phone all reach a
    /// tile the same way and an id nothing offers gets the same answer from all three.</remarks>
    public Task<TileActionResult> InvokeActionAsync(string id) =>
        Content is ITileActions actions
            ? actions.InvokeAsync(id)
            : Task.FromResult(TileActionResult.Refused("This tile offers nothing to do."));

    [RelayCommand]
    private async Task RestartTerminalAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Restart shell?"))
            return;

        await InvokeActionAsync(TileActionIds.Restart);
    }

    [RelayCommand]
    private async Task ResetTileIdAsync()
    {
        if (ConfirmAction != null && !await ConfirmAction("Generate new Tile ID and restart shell?"))
            return;

        // The id first, and nothing has to be pushed into the content: it reads this property through
        // the function its context was built with, so the restart below already launches under the new
        // session.
        TileId = Guid.NewGuid().ToString();
        NotifyLayoutChanged();
        await InvokeActionAsync(TileActionIds.Restart);
    }

    [RelayCommand]
    private void SplitHorizontal() => Split(Orientation.Horizontal);

    [RelayCommand]
    private void SplitVertical() => Split(Orientation.Vertical);

    /// <summary>
    /// Takes the kind the user picked, or shows whatever that kind asks for first.
    /// </summary>
    /// <remarks>
    /// Which kinds have a step of their own is the kinds' business, not this class's: it used to know
    /// that a terminal has profiles, which is the branch on a kind that the registry exists to remove.
    /// </remarks>
    [RelayCommand]
    private void SelectKind(string? kindId)
    {
        if (KindId != TileKindIds.None || kindId is not { Length: > 0 }) return;
        if (_catalog?.Kind(kindId) is not { } kind || _context is not { } context) return;

        var options = kind.SetupOptions(context);
        if (options.Count > 0)
        {
            SetupOptions = options;
            _setupKindId = kindId;
            OnPropertyChanged(nameof(SetupOptions));
            IsChoosingSetup = true;
            return;
        }

        Adopt(kindId, state: null);
    }

    /// <summary>
    /// Takes the option the user picked in a kind's own step.
    /// </summary>
    /// <remarks>
    /// The same call as every other way of creating a tile, because choosing an option <em>is</em>
    /// handing a new tile its initial state — the very thing a saved layout does. Two branches that must
    /// produce identical results, with nothing checking that they do, is how they drift.
    /// </remarks>
    [RelayCommand]
    private void SelectSetupOption(TileSetupOption option)
    {
        var kindId = _setupKindId;
        CancelSetup();
        Adopt(kindId, option.State);
    }

    [RelayCommand]
    private void CancelSetup()
    {
        IsChoosingSetup = false;
        SetupOptions = [];
        _setupKindId = TileKindIds.None;
        OnPropertyChanged(nameof(SetupOptions));
    }

    /// <summary>Gives an empty tile its content, its kind and its name.</summary>
    private void Adopt(string kindId, JsonObject? state)
    {
        if (_catalog?.Kind(kindId) is not { } kind || _context is not { } context) return;

        Content = kind.Create(context, state);
        KindId = kindId;
        TileName = _nameFactory?.Invoke(kindId) ?? kind.DisplayName;
        (Content as IFileContent)?.RenameFile(TileName);
        NotifyLayoutChanged();
    }

    /// <summary>
    /// Opens a tile of <paramref name="kindId"/> beside this one, and answers with it.
    /// </summary>
    /// <remarks>An empty tile takes the content itself rather than splitting: a tile with nothing in it
    /// is a place for a tile, and splitting one leaves an empty half nobody asked for.</remarks>
    public LeafTileNodeViewModel OpenBeside(string kindId, JsonObject? state)
    {
        if (KindId == TileKindIds.None)
        {
            Adopt(kindId, state);
            return this;
        }

        var leaf = Split(Orientation.Horizontal);
        leaf.Adopt(kindId, state);
        return leaf;
    }

    private LeafTileNodeViewModel Split(Orientation orientation)
    {
        // A split puts a tile beside this one, and a maximized tile is the only thing on screen — so the
        // new tile would be created, focused and invisible. Restoring first is also what keeps the
        // soloed splits honest: this call inserts a new split above this leaf, which the scope's
        // remembered path knows nothing about.
        MaximizeScope?.Restore();

        var newLeaf = new LeafTileNodeViewModel(TileKindIds.None, null, _workingDirectory,
            _activationScope, _catalog, _context, _nameFactory)
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
            newLeaf.MaximizeScope = MaximizeScope;
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
        return newLeaf;
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
