using mTiles.Services.Shells;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// What every kind needs in order to build a tile.
/// </summary>
/// <param name="WorkingDirectory">The workspace's directory. Where a git tile looks, where a terminal
/// starts, and what a note's file path is computed from.</param>
/// <param name="Settings">The application's settings service, which most tiles follow for fonts and
/// theme.</param>
/// <param name="RequestSave">Called when a tile changes something that belongs in the layout file.
/// This is the old <c>TileSettingsChanged</c>, which was wired by hand on two kinds and by a different
/// hand on the path that restored them.</param>
/// <param name="OpenSettings">Opens the application's settings dialog on a given tab (see
/// <see cref="SettingsTabs"/>). This is the old <c>OpenDatabaseSettings</c>, which the database tile's
/// own view had to satisfy by reaching up the visual tree for the main window's view model.</param>
/// <remarks>
/// Dependencies, not capabilities. Nothing here is something a consumer interrogates a tile about, which
/// is why it is handed in at construction rather than announced by an interface the tile implements.
/// </remarks>
public sealed record TileContext(
    string WorkingDirectory,
    SettingsService Settings,
    Action? RequestSave = null,
    Action<int>? OpenSettings = null)
{
    /// <summary>
    /// The owning tile's persistent identity — <b>a function rather than a value</b>.
    /// </summary>
    /// <remarks>
    /// The id belongs to the <see cref="LeafTileNodeViewModel"/> that holds the content, and it moves
    /// under it: "New session" replaces the id of a tile whose terminal keeps running. Read at the
    /// moment it matters — which for a terminal is the launch — nothing has to be re-stamped.
    /// <para>Which is also why the function is bound to the tile it was built for and content is never
    /// moved between two of them: dragging one tile onto another exchanges the two leaves' places in
    /// the tree (<c>TileDragDrop.SwapPlaces</c>), so content, id and owner stay together.</para>
    /// <para>The default answers with nothing, which is right for every kind that does not use it and
    /// for a context built without a tile behind it. The tile fills it in with
    /// <c>context with { TileId = () =&gt; TileId }</c> once, in its constructor.</para>
    /// </remarks>
    public Func<string> TileId { get; init; } = static () => "";

    private readonly GitWatcherCache _gitWatcherCache = new();

    /// <summary>
    /// The one watcher over this workspace's working tree, shared by every tile in it.
    /// </summary>
    /// <remarks>Built on first use and held in a field rather than passed in as a value, for the reason
    /// <see cref="Shells"/> is: a record's <c>with</c> copies fields by reference, so the copy a tile
    /// makes of its context reaches the same watcher rather than starting a second one over the same
    /// tree.</remarks>
    public WorkspaceGitWatcher GitWatcher => _gitWatcherCache.Get(
        WorkingDirectory,
        // The watcher keeps its own noise floor rather than waiting for a git tile to supply one, and
        // it asks through the git this installation is configured with, exactly as the git tile does.
        directory => new GitService(directory, GitService.ResolveGitPath(Settings.Settings.GitPath)));

    private readonly ShellCache _shellCache = new();

    /// <summary>
    /// The shells this machine has, detected at most once every <see cref="ShellCache.Ttl"/>.
    /// </summary>
    /// <remarks>
    /// <para>Detection walks every directory on <c>PATH</c> and stats a handful of fixed locations, and
    /// it happens on the UI thread while a workspace is being restored, so a workspace holding eight
    /// saved terminals must not pay for it eight times. Asked for lazily, because a workspace with no
    /// terminal in it never asks; and held in a field rather than passed in as a value, so <c>with</c>
    /// carries the same cache — and the same single detection — to every copy a tile makes of its
    /// context.</para>
    /// <para><b>Over a window, not for the life of the workspace.</b> The same list also answers for a
    /// terminal the user adds by hand, and a workspace stays open for days: cached outright, a shell
    /// installed this afternoon would be missing from the chooser until the application was restarted,
    /// which is not a connection anybody would make. Thirty seconds is what
    /// <c>AiAgentCatalog.Locate</c> holds its own scan for, and the two answer the same kind of
    /// question about the same machine.</para>
    /// </remarks>
    public IReadOnlyList<ShellInstallation> Shells => _shellCache.Get();

    /// <summary>The watcher, made once per working directory and shared by every copy of the context.
    /// Keyed on the directory because <c>with { WorkingDirectory = ... }</c> is a different
    /// workspace, and a watcher over the old one would report changes nobody is looking at.</summary>
    private sealed class GitWatcherCache
    {
        private readonly Lock _gate = new();
        private WorkspaceGitWatcher? _watcher;

        public WorkspaceGitWatcher Get(
            string workingDirectory, Func<string, IIgnoredDirectorySource> ignoredDirectories)
        {
            lock (_gate)
            {
                if (_watcher is { } current
                    && string.Equals(current.WorkingDirectory, workingDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    return current;

                _watcher?.Dispose();
                return _watcher =
                    new WorkspaceGitWatcher(workingDirectory, ignoredDirectories(workingDirectory));
            }
        }
    }

    /// <summary>What the context remembers of the last detection, shared by every copy of it.</summary>
    /// <remarks>A class rather than a pair of fields, because a record's <c>with</c> copies fields by
    /// value: two mutable fields would be copied at the moment a tile made its own context and the
    /// copies would then expire independently, which is a cache per tile wearing the name of a cache per
    /// workspace. A reference is copied as a reference.</remarks>
    private sealed class ShellCache
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

        private readonly Lock _gate = new();
        private IReadOnlyList<ShellInstallation>? _shells;
        private DateTime _detectedAt;

        public IReadOnlyList<ShellInstallation> Get()
        {
            lock (_gate)
            {
                if (_shells is not null && DateTime.UtcNow - _detectedAt < Ttl)
                    return _shells;

                _shells = ShellTerminalCatalog.Detect();
                _detectedAt = DateTime.UtcNow;
                return _shells;
            }
        }
    }
}
