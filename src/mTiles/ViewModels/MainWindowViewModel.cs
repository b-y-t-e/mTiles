using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using mTiles.Services.Shells;
using mTiles.Services.Tiles;

namespace mTiles.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;
    private readonly TileCatalog _catalog;
    private readonly AgentFileSyncCoordinator? _agentFileSync;
    private readonly UpdateService _updateService;
    private readonly Dictionary<string, WorkspaceViewModel> _workspaceCache = new();
    private readonly IProcessMemoryProbe _memoryProbe;
    private readonly Avalonia.Threading.DispatcherTimer _memoryTimer;
    private bool _memorySampleInFlight;

    /// <summary>How often a loaded workspace's memory reading is refreshed.</summary>
    /// <remarks>Slow on purpose. The figure is read to decide whether to unload something, not to watch
    /// a build allocate, and each reading walks the machine's whole process table.</remarks>
    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>The application's dictation service, null when it was never wired up.</summary>
    internal Services.Speech.DictationService? Dictation { get; }

    /// <summary>The phone bridge, null when it was never wired up.</summary>
    internal Services.Phone.PhoneBridgeManager? PhoneBridge { get; }

    /// <summary>The CLAUDE.md/AGENTS.md sync coordinator, null when it was never wired up. Exposed so
    /// the shell can wire its one dialog Func.</summary>
    internal AgentFileSyncCoordinator? AgentFileSync => _agentFileSync;

    /// <summary>
    /// Whether to offer the QR button at all.
    /// </summary>
    /// <remarks>
    /// Tied to dictation, because that is what it is a way of doing. The bridge is built unconditionally
    /// — it opens nothing until somebody asks — so asking only whether it exists meant the button was
    /// always there, including for somebody who had turned dictation off and would find a panel offering
    /// to set up a microphone they had just declined.
    /// <para>Deliberately not conditioned on a model being downloaded: the panel is also where the
    /// feature is discovered, and hiding the way in until it is fully set up leaves nothing to find.</para>
    /// </remarks>
    public bool HasPhoneBridge =>
        PhoneBridge is not null && _settingsService.Settings.Speech.Enabled;

    /// <summary>
    /// Opens the QR panel. Wired from the view, which is the only thing holding a window to parent it to.
    /// </summary>
    /// <remarks>
    /// <b>A window-level entry point, not a per-tile one.</b> It began in the tile header beside the
    /// microphone, which read as "dictate into <em>this</em> tile" — a promise the feature does not make
    /// and cannot: the phone sends to whichever tile is active when you speak, exactly as the keyboard
    /// shortcut does. Sitting next to Settings, it says what it is: a way to reach the application, not a
    /// property of one tile.
    /// </remarks>
    public Func<Task>? ShowPhoneBridge { get; set; }

    [RelayCommand]
    private Task ShowPhoneBridgeAsync() =>
        PhoneBridge is not null && ShowPhoneBridge is { } show ? show() : Task.CompletedTask;

    [ObservableProperty]
    private WorkspacesPanelViewModel _workspacesPanel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private WorkspaceViewModel? _currentWorkspace;

    /// <summary>What the window is called — see <see cref="WindowTitle"/>.</summary>
    public string Title => WindowTitle.For(CurrentWorkspace?.Name);

    /// <summary>
    /// Raised when the tile a window-level command acts on changes, or when that tile's own state does.
    /// </summary>
    /// <remarks>
    /// The workspace raises it for its own tiles; this follows whichever workspace is on screen, so a
    /// listener subscribes once and never learns that workspaces exist. Switching workspaces is itself
    /// such a change — the active tile becomes another workspace's, or none.
    /// </remarks>
    public event Action? ActiveTileChanged;

    partial void OnCurrentWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.ActiveTileChanged -= RaiseActiveTileChanged;
        if (newValue is not null)
            newValue.ActiveTileChanged += RaiseActiveTileChanged;

        RaiseActiveTileChanged();
    }

    private void RaiseActiveTileChanged() => ActiveTileChanged?.Invoke();

    [ObservableProperty]
    private SettingsViewModel _settings;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateVersion = "";

    /// <param name="catalog">Every kind of tile the application can build, registered once at
    /// startup.</param>
    public MainWindowViewModel(WorkspaceService workspaceService, PersistenceService persistenceService,
        SettingsService settingsService, TileCatalog catalog, DatabaseServiceManager? dbManager = null,
        Services.Speech.DictationService? dictation = null,
        Services.Phone.PhoneBridgeManager? phoneBridge = null,
        IProcessMemoryProbe? memoryProbe = null,
        AgentFileSyncCoordinator? agentFileSync = null)
    {
        _memoryProbe = memoryProbe ?? new ProcessTreeMemory();
        _persistenceService = persistenceService;
        _settingsService = settingsService;
        _catalog = catalog;
        _agentFileSync = agentFileSync;

        // The switch lives on another dialog, so nothing else would tell this to look again — and a
        // button that appears only after a restart reads as a broken setting.
        _settingsService.SettingsChanged += OnSettingsChanged;
        Dictation = dictation;
        PhoneBridge = phoneBridge;
        _updateService = new UpdateService();
        _workspacesPanel = new WorkspacesPanelViewModel(workspaceService, settingsService, _agentFileSync);
        _settings = new SettingsViewModel(settingsService, dbManager, dictation);

        // An install command runs in a tile, and only this object knows which workspace is open. Null
        // when there is none, which the settings page answers by showing the command instead of
        // running it — never by running it somewhere the user cannot see.
        _settings.RunInstallPlan = plan =>
        {
            if (CurrentWorkspace is not { } workspace) return Task.FromResult(false);

            // InstallCommand, not CommandLine: the latter is the readable form, and its own remarks say
            // it is never what runs. It was - and the Sign in button, whose command is a whole shell
            // line, arrived in the tile wrapped in quotes and was echoed back rather than run.
            var shell = ShellTerminalCatalog.ResolveDefault(_settingsService.Settings).Shell;

            workspace.OpenTileBesideActive(TileKindIds.Terminal, new System.Text.Json.Nodes.JsonObject
            {
                [TerminalTileKind.StartupScriptKey] = InstallCommand.For(plan, shell),
            });
            return Task.FromResult(true);
        };

        _updateService.UpdateAvailable += () =>
        {
            IsUpdateAvailable = true;
            UpdateVersion = _updateService.NewVersion ?? "";
        };
        _updateService.StartPeriodicCheck();

        _workspacesPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspacesPanelViewModel.SelectedWorkspace))
                SwitchToWorkspace(_workspacesPanel.SelectedWorkspace?.Workspace);
        };
        _workspacesPanel.Workspaces.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var id in _workspaceCache.Keys.ToList())
                    OnWorkspaceRemoved(id);
                // A file-watching sync engine outlives the workspace view model: the panel's toggle
                // starts one for a row that was never opened, which OnWorkspaceRemoved cannot reach.
                _agentFileSync?.UnloadAll();
            }
            // Remove only, and not "anything with OldItems": a Move carries the item it moved in
            // OldItems too, so re-ordering the list would dispose the workspace's tiles.
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                     && e.OldItems != null)
            {
                foreach (WorkspaceItemViewModel item in e.OldItems)
                {
                    OnWorkspaceRemoved(item.Id);
                    // By path as well as by id: a row the user never opened has no view model here, and
                    // its sync engine (started by the panel's toggle alone) would otherwise go on
                    // mirroring the files of a workspace that is no longer on the list.
                    _agentFileSync?.Unload(item.DirectoryPath);
                }
            }
        };

        _workspacesPanel.UnloadWorkspace = UnloadWorkspaceAsync;

        _memoryTimer = new Avalonia.Threading.DispatcherTimer { Interval = MemorySampleInterval };
        _memoryTimer.Tick += async (_, _) =>
        {
            // One sample at a time. A reading walks the machine's whole process table, so on a busy
            // machine it can outlive the interval — and an unguarded tick would stack those walks up
            // exactly when there is least room for them, with the row writes landing out of order.
            // A plain field, because both the test and the write back happen on the UI thread.
            if (_memorySampleInFlight) return;
            _memorySampleInFlight = true;
            try { await SampleMemoryAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Memory sampling failed: {0}", ex.Message); }
            finally { _memorySampleInFlight = false; }
        };
        _memoryTimer.Start();

        if (_workspacesPanel.Workspaces.Count > 0)
        {
            var lastId = _settingsService.Settings.LastWorkspaceId;
            var target = _workspacesPanel.Workspaces.FirstOrDefault(w => w.Id == lastId)
                         ?? _workspacesPanel.Workspaces[0];
            _workspacesPanel.SelectedWorkspace = target;
        }
    }

    [RelayCommand]
    private async Task ToggleSettingsAsync()
    {
        if (!IsSettingsOpen)
        {
            IsSettingsOpen = true;
            return;
        }

        await CloseSettingsAsync();
    }

    /// <summary>
    /// Whether the application may shut down, asking about anything left unapplied first.
    /// <para>Closing the window is the commonest way of saying "I'm done", and without this the
    /// protection on the settings dialog has a hole exactly there — the changes would survive in a view
    /// model that is about to be thrown away. It asks whether or not the dialog is open, because an
    /// unapplied change does not stop being one when its window is out of sight.</para>
    /// </summary>
    /// <returns>False when the user chose to go back, in which case the dialog has been put in front
    /// of them and the close must be called off.</returns>
    public async Task<bool> ConfirmShutdownAsync()
    {
        if (!Settings.HasUnsavedDatabaseChanges)
            return true;

        if (await Settings.TryCloseAsync())
            return true;

        // They went back. Put the dialog in front of them, or "go back" leads nowhere visible.
        IsSettingsOpen = true;
        return false;
    }

    /// <summary>
    /// The one way the settings dialog closes. There are three gestures for it — the close button,
    /// Escape, and clicking outside — and each used to set <see cref="IsSettingsOpen"/> for itself, so
    /// anything that has to happen on the way out would have had to be written three times and stay
    /// written three times.
    /// </summary>
    /// <returns>False when the dialog is still open because the user chose to go back to it.</returns>
    public async Task<bool> CloseSettingsAsync()
    {
        if (!IsSettingsOpen)
            return true;

        if (!await Settings.TryCloseAsync())
            return false;

        IsSettingsOpen = false;
        return true;
    }

    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>Shows the settings dialog on a given tab, for a tile that has settings of its own
    /// elsewhere.</summary>
    /// <remarks>Handed down to every workspace and from there into each tile's context. The database
    /// tile's view used to do this by walking up the visual tree until it found a window whose data
    /// context was this class — which worked, and told the view about a view model two levels above the
    /// one it was drawing.</remarks>
    private void OpenSettingsOn(int tab)
    {
        Settings.SelectedTab = tab;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!_updateService.HasUpdate) return;
        var confirm = ConfirmAction;
        if (confirm != null)
        {
            var accepted = await confirm($"Version {_updateService.NewVersion} is ready. Restart now to update?");
            if (!accepted) return;
        }
        _updateService.ApplyUpdate();
    }

    public event Action<string>? WorkspaceRemoved;

    private void OnSettingsChanged() => OnPropertyChanged(nameof(HasPhoneBridge));

    public void DisposeAll()
    {
        _memoryTimer.Stop();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _updateService.Dispose();
        foreach (var vm in _workspaceCache.Values)
            vm.Dispose();
        _workspaceCache.Clear();
        _workspacesPanel.Dispose();
        // The tiles above may have queued a .gitignore edit on their way out, and that chain is the one
        // thing here nobody else waits on: abandoned mid-write it leaves a .gitignore.mtiles-tmp in the
        // user's repository. Bounded — a shutdown is not held up for housekeeping.
        Services.GitIgnoreEditQueue.WaitForAll(TimeSpan.FromSeconds(2));
    }

    private void OnWorkspaceRemoved(string workspaceId)
    {
        if (!_workspaceCache.Remove(workspaceId, out var vm)) return;

        vm.PropertyChanged -= OnWorkspaceActivityChanged;
        _agentFileSync?.Unload(vm.WorkingDirectory);
        vm.Dispose();
        ShowUnloadedInPanel(workspaceId);
        WorkspaceRemoved?.Invoke(workspaceId);
    }

    /// <summary>Puts a row back to how an unopened workspace looks.</summary>
    /// <remarks>Both properties together, because a row saying it holds 400 MB while nothing of it is
    /// running is worse than a row saying nothing: the figure is the whole reason somebody would unload
    /// it, and one left standing reads as an unload that did not work.</remarks>
    private void ShowUnloadedInPanel(string workspaceId)
    {
        if (FindRow(workspaceId) is not { } row) return;
        row.IsLoaded = false;
        row.MemoryText = "";
        row.IsBusy = false;
    }

    private WorkspaceItemViewModel? FindRow(string workspaceId) =>
        _workspacesPanel.Workspaces.FirstOrDefault(w => w.Id == workspaceId);

    /// <summary>Gives a workspace's memory back without giving up the workspace.</summary>
    /// <remarks>
    /// <para>Asks first, and an unwired <see cref="ConfirmAction"/> answers no: the tiles being closed
    /// are running shells, and a shell killed by a mis-click in a context menu takes whatever it was in
    /// the middle of with it. The layout is on disk, so what comes back on the next click is the same
    /// set of tiles — but not the same sessions.</para>
    /// <para>The selection is cleared first when this is the workspace on screen. Disposing the view
    /// model out from under a selection that still names it would leave the row highlighted, the view
    /// gone and nothing able to bring it back: re-selecting a workspace that is already selected raises
    /// no change and so builds nothing.</para>
    /// </remarks>
    private async Task UnloadWorkspaceAsync(WorkspaceItemViewModel item)
    {
        if (!_workspaceCache.ContainsKey(item.Id)) return;
        if (ConfirmAction is not { } confirm) return;
        if (!await confirm($"Unload workspace \"{item.Name}\"?\n\nIts tiles are closed and everything running in them stops."))
            return;

        // Checked again: the confirmation is a dialog, and a workspace can be removed while it is open.
        if (!_workspaceCache.ContainsKey(item.Id)) return;

        if (ReferenceEquals(_workspacesPanel.SelectedWorkspace, item))
            _workspacesPanel.SelectedWorkspace = null;

        OnWorkspaceRemoved(item.Id);
    }

    /// <summary>Takes a memory reading for every loaded workspace and puts it on its row.</summary>
    /// <remarks>
    /// <para>The tree walk that collects the process ids is on the UI thread — it reads the tile tree,
    /// which is the UI thread's — and the reading of the machine's process table is not, because that is
    /// a few hundred processes opened one at a time and doing it between two frames is a stutter every
    /// few seconds.</para>
    /// <para>A row that has gone by the time the reading comes back is simply not found, and one that
    /// was unloaded meanwhile is skipped: the reading describes a workspace that no longer has anything
    /// running, and writing it would undo <see cref="ShowUnloadedInPanel"/> from a task started before
    /// the unload.</para>
    /// <para>Internal so a test can drive one sample with a probe of its own: the grouping by workspace
    /// id and the skipping of what is not loaded are the joins this class alone makes.</para>
    /// </remarks>
    internal async Task SampleMemoryAsync()
    {
        var roots = _workspaceCache.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyCollection<int>)entry.Value.ChildProcessIds.ToList());
        if (roots.Count == 0) return;

        // Every workspace in one call: the answer comes out of the machine's whole process table, so
        // asking per workspace would read that table once per loaded workspace, every five seconds.
        var readings = await Task.Run(() => _memoryProbe.WorkingSetsOf(roots));

        foreach (var (workspaceId, bytes) in readings)
        {
            if (!_workspaceCache.ContainsKey(workspaceId)) continue;
            if (FindRow(workspaceId) is { } row)
                row.MemoryText = MemoryDisplay.Format(bytes);
        }
    }

    /// <summary>Keeps a panel row's "working" light in step with the tiles of its workspace.</summary>
    /// <remarks>Wired here because this is the one place a workspace's view model comes into existence,
    /// and the row and the view model are the two halves that never meet anywhere else. Both are
    /// discarded together when the workspace is removed, so the subscription outlives neither.</remarks>
    private void ShowActivityInPanel(WorkspaceViewModel workspace)
    {
        // The row is looked up for the first reading only, and its absence is not a reason to skip
        // the subscription: the handler finds the row itself on every event, so a workspace whose row
        // has not been added yet — or has been replaced since — starts lighting up as soon as it is
        // there. Returning early left that workspace's light dead for the life of the window.
        if (FindRow(workspace.WorkspaceId) is { } row)
        {
            row.IsBusy = workspace.IsBusy;
            // Said here rather than in SwitchToWorkspace because this is the one method a workspace's
            // view model coming into existence goes through, and "loaded" is exactly that fact.
            row.IsLoaded = true;
        }

        // A named handler, not a lambda. The two objects are discarded together today, so nothing
        // leaks — but "nothing leaks" was resting on that being true rather than on anything saying
        // so, and a lambda cannot be taken off if it stops being true. Detached in
        // OnWorkspaceRemoved, which is the one place a workspace view model is let go of.
        workspace.PropertyChanged += OnWorkspaceActivityChanged;
    }

    /// <summary>Carries a workspace's "working" light to its row in the panel.</summary>
    /// <remarks>
    /// The row is looked up per event rather than captured, so this handler belongs to no particular
    /// pair and can be removed with the sender alone. A row that has gone is simply not found.
    /// </remarks>
    private void OnWorkspaceActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.IsBusy)) return;
        if (sender is not WorkspaceViewModel workspace) return;

        if (FindRow(workspace.WorkspaceId) is { } row)
            row.IsBusy = workspace.IsBusy;
    }

    private void SwitchToWorkspace(Workspace? workspace)
    {
        if (workspace == null)
        {
            CurrentWorkspace = null;
            return;
        }

        if (!_workspaceCache.TryGetValue(workspace.Id, out var vm))
        {
            vm = new WorkspaceViewModel(workspace, _persistenceService, _settingsService, _catalog,
                Dictation, OpenSettingsOn, _agentFileSync);
            _workspaceCache[workspace.Id] = vm;
            ShowActivityInPanel(vm);
        }

        CurrentWorkspace = vm;
        vm.ActivateLastTile();
        _settingsService.Settings.LastWorkspaceId = workspace.Id;
        _settingsService.DebouncedSave();
    }
}
