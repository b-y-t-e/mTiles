using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using mTiles.Services.Tiles;

namespace mTiles.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;
    private readonly TileCatalog _catalog;
    private readonly UpdateService _updateService;
    private readonly Dictionary<string, WorkspaceViewModel> _workspaceCache = new();

    /// <summary>The application's dictation service, null when it was never wired up.</summary>
    internal Services.Speech.DictationService? Dictation { get; }

    /// <summary>The phone bridge, null when it was never wired up.</summary>
    internal Services.Phone.PhoneBridgeManager? PhoneBridge { get; }

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
    private WorkspaceViewModel? _currentWorkspace;

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
        Services.Phone.PhoneBridgeManager? phoneBridge = null)
    {
        _persistenceService = persistenceService;
        _settingsService = settingsService;
        _catalog = catalog;

        // The switch lives on another dialog, so nothing else would tell this to look again — and a
        // button that appears only after a restart reads as a broken setting.
        _settingsService.SettingsChanged += OnSettingsChanged;
        Dictation = dictation;
        PhoneBridge = phoneBridge;
        _updateService = new UpdateService();
        _workspacesPanel = new WorkspacesPanelViewModel(workspaceService, settingsService);
        _settings = new SettingsViewModel(settingsService, dbManager, dictation);

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
            }
            // Remove only, and not "anything with OldItems": a Move carries the item it moved in
            // OldItems too, so re-ordering the list would dispose the workspace's tiles.
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                     && e.OldItems != null)
            {
                foreach (WorkspaceItemViewModel item in e.OldItems)
                    OnWorkspaceRemoved(item.Id);
            }
        };

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
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _updateService.Dispose();
        foreach (var vm in _workspaceCache.Values)
            vm.Dispose();
        _workspaceCache.Clear();
        _workspacesPanel.Dispose();
    }

    private void OnWorkspaceRemoved(string workspaceId)
    {
        if (!_workspaceCache.Remove(workspaceId, out var vm)) return;

        vm.PropertyChanged -= OnWorkspaceActivityChanged;
        vm.Dispose();
        WorkspaceRemoved?.Invoke(workspaceId);
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
        if (_workspacesPanel.Workspaces.FirstOrDefault(w => w.Id == workspace.WorkspaceId) is { } row)
            row.IsBusy = workspace.IsBusy;

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

        if (_workspacesPanel.Workspaces.FirstOrDefault(w => w.Id == workspace.WorkspaceId) is { } row)
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
                Dictation, OpenSettingsOn);
            _workspaceCache[workspace.Id] = vm;
            ShowActivityInPanel(vm);
        }

        CurrentWorkspace = vm;
        vm.ActivateLastTile();
        _settingsService.Settings.LastWorkspaceId = workspace.Id;
        _settingsService.DebouncedSave();
    }
}
