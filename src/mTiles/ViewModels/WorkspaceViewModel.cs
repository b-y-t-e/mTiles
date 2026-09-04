using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using System.Linq;

namespace mTiles.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;
    private readonly TileCatalog _catalog;
    private readonly TileContext _tileContext;
    private readonly TileTreeSerializer _serializer;
    private readonly Services.Speech.DictationService? _dictation;

    [ObservableProperty]
    private TileNodeViewModel? _rootTile;

    private LeafTileNodeViewModel? _lastActiveLeaf;

    /// <summary>Whether this workspace's file must be left exactly as it was found — see
    /// <see cref="ScheduleSave"/> and <c>docs/TILES.md</c> → the third migration rule.</summary>
    private readonly bool _savingWouldLoseATile;

    /// <summary>So the refusal is traced once rather than on every splitter drag.</summary>
    private bool _refusedToSaveLayout;

    /// <summary>Whether anything in this workspace is working — what the panel's row light shows.</summary>
    /// <remarks>Any tile, because the question the list answers is "is there something going on in
    /// there", and a workspace with one busy tile out of four is a workspace to go back to.</remarks>
    public bool IsBusy => EnumerateLeaves(RootTile).Any(leaf => leaf.IsBusy);

    /// <summary>Every process this workspace's tiles have started.</summary>
    /// <remarks>The roots only: what those processes went on to spawn is the business of whoever reads
    /// the machine's process table (<see cref="Services.IProcessMemoryProbe"/>), and a workspace that
    /// had to know about descendants would be a workspace that knew about operating systems.</remarks>
    public IEnumerable<int> ChildProcessIds =>
        EnumerateLeaves(RootTile)
            .Select(leaf => leaf.ChildProcessId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value);

    /// <summary>
    /// Raised when the tile a window-level command acts on changes, or when that tile's own state does.
    /// </summary>
    /// <remarks>
    /// One event for both, because everything listening — the phone bridge is the first — has to redraw
    /// on either: a phone showing Git's buttons under the caption "Git#1" is as wrong when the user moves
    /// to another tile as it is when Continue becomes available in the tile it is already looking at.
    /// Raised for the tile the workspace <em>resolves</em> as active rather than for whichever leaf spoke,
    /// so a background tile ticking away costs nothing.
    /// </remarks>
    public event Action? ActiveTileChanged;

    partial void OnRootTileChanged(TileNodeViewModel? value)
    {
        OnPropertyChanged(nameof(IsBusy));

        // A closed tile or a rebuilt tree can leave nothing active at all, and "nothing" is a state a
        // listener has to be told about — it is the difference between a stale set of buttons and none.
        ActiveTileChanged?.Invoke();
    }

    public string WorkspaceId { get; }
    public string WorkingDirectory { get; }

    private readonly TileActivationScope _activationScope = new();

    /// <summary>Which of this workspace's tiles has the whole of it, if any.</summary>
    /// <remarks>Per workspace like the activation scope beside it, and for the same reason: switching to
    /// another workspace must not put a maximized tile away, and coming back must find it as it was
    /// left.</remarks>
    private readonly TileMaximizeScope _maximizeScope = new();

    /// <summary>Every name this workspace has given a tile, by kind id.</summary>
    /// <remarks>A dictionary rather than a field per kind: five fields meant five parameters on the
    /// allocator and a five-armed <c>else if</c> reading them back out of a saved layout, and a seventh
    /// kind meant finding all three places again. Names rather than counters, because what a kind makes
    /// of them is the kind's own business — a number for most, an adjective and an animal for a
    /// terminal.</remarks>
    private readonly Dictionary<string, HashSet<string>> _namesPerKind =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="catalog">Every kind of tile this workspace can build.</param>
    /// <param name="openSettings">Opens the application's settings dialog on a tab. Handed down rather
    /// than reached for: the database tile's view used to walk up the visual tree to the main window's
    /// view model to do this.</param>
    public WorkspaceViewModel(Workspace workspace, PersistenceService persistenceService,
        SettingsService settingsService, TileCatalog catalog,
        Services.Speech.DictationService? dictation = null, Action<int>? openSettings = null)
    {
        WorkspaceId = workspace.Id;
        WorkingDirectory = workspace.DirectoryPath;
        _persistenceService = persistenceService;
        _settingsService = settingsService;
        _dictation = dictation;
        _catalog = catalog;
        _tileContext = new TileContext(WorkingDirectory, settingsService, ScheduleSave, openSettings);

        _serializer = new TileTreeSerializer(
            _catalog,
            _tileContext,
            AllocateTileName,
            ConfigureLeafCallbacks,
            _activationScope);

        var state = persistenceService.LoadLayout(workspace.Id);
        if (state?.RootTile != null)
        {
            RememberSavedNames(state.RootTile);

            // Before the tree is built, because it rewrites what a leaf *is*: an AI CLI that was a
            // shell profile becomes an agent tile. Without it this stage takes the profiles away and
            // every AI tile anybody has comes back as a bare shell.
            var becameAgents = AgentTileMigration.Apply(state.RootTile, settingsService.Settings);

            var load = _serializer.Deserialize(state.RootTile, OnLayoutChanged);
            RootTile = load.Root;
            _lastActiveLeaf = load.ActiveLeaf;

            // Nothing is written for the rest of this workspace's life once a leaf names a kind this
            // build does not have: that tile is shown as empty, and an empty tile written over it is
            // how a layout is lost for good. Set before anything can ask for a save, so the migrating
            // one below and every later one are refused alike — and with nothing written there is
            // nothing to take a copy ahead of either.
            _savingWouldLoseATile = load.HasUnknownKind;

            if ((load.NeedsSave || becameAgents) && !_savingWouldLoseATile)
            {
                persistenceService.BackupBeforeKindMigration(workspace.Id);

                // A second copy, and only when this migration is the one that ran. The file on disk is
                // still the pre-migration one at this point — nothing has been written yet — so both
                // copies are of what the user had.
                if (becameAgents)
                    persistenceService.BackupBeforeAgentMigration(workspace.Id);

                ScheduleSave();
            }
        }
        else
        {
            RootTile = CreateEmptyLeaf();
        }

        // Once per workspace, and before anything is written: the section the old writer left in
        // claude.local.md and AGENTS.md names this machine's servers and its bridge port.
        LegacyDatabaseSectionCleanup.Run(WorkingDirectory);

        // A restored layout reaches here without ever asking for a save, so this is the only pass that
        // a workspace opened and left alone would get.
        SyncAgentFiles();
        WithdrawDatabaseSkillIfNoTileOffersIt();
    }

    /// <summary>
    /// Clears the database skill from every directory any agent reads when this workspace no longer
    /// holds a database tile.
    /// </summary>
    /// <remarks>The blind delete is otherwise reachable only from a database tile or from the service
    /// being stopped, and a tile removed from the layout between two sessions is neither: the next
    /// session opens with no tile to notice, so a <c>SKILL.md</c> naming this machine's servers and its
    /// bridge port would sit there advertising a bridge that grants nothing. Same rule as the section
    /// cleanup above — no agent may find out about a bridge that is no longer there.
    /// <para>Guarded by the tiles rather than run unconditionally, because a database tile writes its
    /// skill from its own constructor, which has already run by the time the tree is loaded.</para>
    /// </remarks>
    private void WithdrawDatabaseSkillIfNoTileOffersIt()
    {
        if (EnumerateLeaves(RootTile).Select(leaf => leaf.Content).OfType<DatabaseTileViewModel>().Any())
            return;

        WorkspaceAgentFiles.RemoveSkillEverywhere(
            WorkingDirectory, Services.Database.DatabaseSkillWriter.SkillName);
    }

    /// <summary>
    /// Tells this workspace's agent-facing files which agents are in here now.
    /// </summary>
    /// <remarks>The tile tree is the source of truth rather than what is installed on the machine: a
    /// project you only ever open Claude Code in has no business growing an <c>.opencode/skills</c>
    /// directory. The set is recomputed whole every time, because three agents share
    /// <c>.agents/skills</c> and "delete the directory of the agent that left" would take it out from
    /// under the two still standing.</remarks>
    private void SyncAgentFiles() =>
        _tileContext.AgentFiles.Follow(EnumerateLeaves(RootTile)
            .Select(leaf => leaf.Content)
            .OfType<AgentTileViewModel>()
            .Select(tile => Services.Agents.AiAgentCatalog.Find(tile.AgentId))
            .OfType<Services.Agents.IAiAgent>());

    private LeafTileNodeViewModel CreateEmptyLeaf()
    {
        var leaf = new LeafTileNodeViewModel(TileKindIds.None, null, WorkingDirectory,
            _activationScope, _catalog, _tileContext, AllocateTileName);
        // Everything else a tile needs is decided in one place, including LayoutChanged. Setting it here
        // as well is the arrangement the whole fix was about removing: two lists to keep in step.
        ConfigureLeafCallbacks(leaf);
        return leaf;
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

    /// <summary>
    /// Opens a tile of <paramref name="kindId"/> beside whichever tile is in use, and focuses it.
    /// </summary>
    /// <remarks>What Settings uses to run an agent's install command where the user can watch it: the
    /// command writes outside every directory this application owns, so it belongs in a tile with its
    /// own scrollback rather than in a process nobody can see. Beside the active tile rather than in
    /// place of it — nothing the user is working in is taken away — and any tile will do when the
    /// workspace has no active one, because one has to.</remarks>
    public void OpenTileBesideActive(string kindId, System.Text.Json.Nodes.JsonObject? state)
    {
        var host = ResolveActiveTile(orAnyTile: true);
        host?.OpenBeside(kindId, state).RequestFocus();
    }

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
        leaf.LayoutChanged = OnLayoutChanged;
        leaf.Dictation = _dictation;
        leaf.MaximizeScope = _maximizeScope;
        // Passed on to every tile, so a tile created by splitting is configured by this same method
        // rather than by whatever its parent remembered to copy.
        leaf.ConfigureNewLeaf = ConfigureLeafCallbacks;
        leaf.RootReplaced = newRoot => RootTile = ConfigureRoot(newRoot);
        leaf.RootCleared = () =>
        {
            // The tree this workspace is about to throw away is the one the soloed splits are on.
            _maximizeScope.Restore();
            RootTile = CreateEmptyLeaf();
            OnLayoutChanged();
        };
        leaf.PropertyChanged -= OnLeafPropertyChanged;
        leaf.PropertyChanged += OnLeafPropertyChanged;
        // A tile arriving is a change to the answer too — splitting anything but the root leaves
        // RootTile alone, so its own notification never fires.
        OnPropertyChanged(nameof(IsBusy));
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeafTileNodeViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(IsBusy));
            return;
        }

        if (e.PropertyName == nameof(LeafTileNodeViewModel.IsActive)
            && sender is LeafTileNodeViewModel leaf && leaf.IsActive)
        {
            _lastActiveLeaf = leaf;
            ActiveTileChanged?.Invoke();
            return;
        }

        // The active tile's own list, or the name it is offered under. The leaf republishes its Actions
        // on any content change at all, deliberately, so this needs no list of the properties each kind
        // computes its enabled flags from — and a tile nobody is aimed at raises nothing here.
        if ((e.PropertyName == nameof(LeafTileNodeViewModel.Actions)
                || e.PropertyName == nameof(LeafTileNodeViewModel.TileName))
            && ReferenceEquals(sender, ActiveTile))
            ActiveTileChanged?.Invoke();
    }

    private TileNodeViewModel ConfigureRoot(TileNodeViewModel node)
    {
        node.LayoutChanged = OnLayoutChanged;
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

    /// <summary>What to call a tile of that kind, given what this workspace already holds.</summary>
    /// <remarks>The kind decides; this only keeps the list of names it has already handed out and
    /// remembers the answer. A tile of no kind has no name yet, and an id nothing is registered under
    /// gets the same answer — there is nothing to ask.</remarks>
    private string AllocateTileName(string kindId)
    {
        if (_catalog.Kind(kindId) is not { } kind) return "";

        var used = UsedNames(kindId);
        var name = kind.NameFor(used);
        used.Add(name);
        return name;
    }

    private HashSet<string> UsedNames(string kindId) =>
        _namesPerKind.TryGetValue(kindId, out var names)
            ? names
            : _namesPerKind[kindId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Picks up the names the saved layout already uses, so a new tile is not called
    /// <c>Git#1</c> beside one already called that.</summary>
    private void RememberSavedNames(TileNode? node)
    {
        if (node == null) return;
        if (!node.IsLeaf)
        {
            RememberSavedNames(node.First);
            RememberSavedNames(node.Second);
            return;
        }

        if (node.TileName is { Length: > 0 } tileName && node.Kind is { Length: > 0 } kindId)
            UsedNames(kindId).Add(tileName);
    }

    /// <summary>The tree itself changed: republish what the agents here read, then write the layout.</summary>
    /// <remarks>Separate from <see cref="ScheduleSave"/>, which is also <c>TileContext.RequestSave</c> —
    /// every tile asking for its own content to be persisted. Only a change to the tree can change which
    /// agents are in here, and only this route may create directories and files in the user's repository.
    /// </remarks>
    private void OnLayoutChanged()
    {
        SyncAgentFiles();
        ScheduleSave();
    }

    /// <summary>Writes this workspace's layout out, unless doing so would lose a tile.</summary>
    /// <remarks>The refusal is here rather than at the one save a migration asks for, because every
    /// other route to this method — a splitter dragged, a tile renamed, split or closed — serialises the
    /// same tree and writes the unknown kind out of the file just as thoroughly. It lasts the session:
    /// nothing in here can restore the kind the catalog does not have, so nothing can make the file safe
    /// to write again until the user is back on the build that wrote it.</remarks>
    private void ScheduleSave()
    {
        if (_savingWouldLoseATile)
        {
            if (!_refusedToSaveLayout)
            {
                _refusedToSaveLayout = true;
                System.Diagnostics.Trace.TraceWarning(
                    "Workspace '{0}' holds a tile of a kind this build does not have; its layout will "
                    + "not be written for the rest of this session.", WorkspaceId);
            }
            return;
        }

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
