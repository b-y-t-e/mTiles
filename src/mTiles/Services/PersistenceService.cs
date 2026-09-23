using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

public sealed class PersistenceService
{
    private readonly string _workspacesDir;

    /// <summary>The layout saves waiting out their debounce, one per layout.</summary>
    /// <remarks><b>One per layout, never one for the service.</b> Every workspace shares this object, and a
    /// single timer meant a save scheduled for workspace B threw away the one still pending for A: a note
    /// added or renamed in A a moment before switching to B was gone from A's layout on the next launch —
    /// and a renamed note's layout still named the file the rename had just moved away from, so the tile
    /// reopened empty.</remarks>
    private readonly Dictionary<string, PendingSave> _pending = new(StringComparer.Ordinal);

    /// <summary>One lock per layout, held for the whole of a write.</summary>
    /// <remarks>So a flush waits for a debounced write already in flight — the tiles are disposed right after
    /// it — and two writes of one layout land one after the other rather than in whichever order their
    /// moves happen to finish.</remarks>
    private readonly Dictionary<string, object> _writeLocks = new(StringComparer.Ordinal);

    private object WriteLockFor(string workspaceId)
    {
        lock (_writeLocks)
        {
            if (!_writeLocks.TryGetValue(workspaceId, out var gate))
                _writeLocks[workspaceId] = gate = new object();
            return gate;
        }
    }

    private sealed class PendingSave(Func<TileNode?> getRootTile)
    {
        public Func<TileNode?> GetRootTile { get; } = getRootTile;
        public Timer? Timer { get; set; }
    }

    /// <summary>What a copy taken before the tile-kind migration is called.</summary>
    /// <remarks>One suffix rather than a timestamp, unlike <c>settings.bad-…</c>: this is taken once,
    /// on the first launch after the update, and the thing worth keeping is the layout as it was
    /// <em>before</em> the rewrite. A second copy could only be of an already-migrated file, which is
    /// no use to anybody and would overwrite the one that is.</remarks>
    private const string PreKindBackupSuffix = ".pre-kind.json";

    /// <summary>What a copy taken before the agent-tile migration is called.</summary>
    /// <remarks>A second suffix rather than reusing the first: the two migrations are a release apart,
    /// so by the time this one runs the pre-kind copy is the file as it was two formats ago and is worth
    /// keeping on its own account. Same rule otherwise — taken once, never overwritten.</remarks>
    private const string PreAgentsBackupSuffix = ".pre-agents.json";

    public PersistenceService() : this(null) { }

    /// <param name="workspacesDirectory">Where tile layouts live. Defaults to the user's own directory;
    /// a test passes a temporary one, for the same reason as the services beside it. Internal.</param>
    internal PersistenceService(string? workspacesDirectory)
    {
        _workspacesDir = workspacesDirectory ?? AppPaths.GetWorkspacesDirectory();
        Directory.CreateDirectory(_workspacesDir);
    }

    public WorkspaceState? LoadLayout(string workspaceId)
    {
        var filePath = GetFilePath(workspaceId);
        if (!File.Exists(filePath)) return null;
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<WorkspaceState>(json, JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Failed to load workspace layout '{0}': {1}", workspaceId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Keeps this workspace's layout as it was before tile kinds existed, once.
    /// </summary>
    /// <remarks>
    /// <para>Moving a leaf's per-kind fields into its kind's own state is the only moment at which every
    /// file under <c>workspaces/</c> is rewritten at once, and a tile layout is the one thing in this
    /// application a user cannot reconstruct from anything else — not from the repository, not from a
    /// note on disk, not from the shell history. <c>settings.json</c> has had this rule for a while
    /// (<c>settings.bad-&lt;timestamp&gt;.json</c>); layouts did not.</para>
    /// <para>It fails soft and it never overwrites: a copy that cannot be taken is a reason to log, not
    /// a reason to refuse to open the workspace, and a second run must not replace the pre-migration
    /// copy with a post-migration one.</para>
    /// </remarks>
    public void BackupBeforeKindMigration(string workspaceId) =>
        BackupOnce(workspaceId, PreKindBackupSuffix);

    /// <summary>
    /// Keeps this workspace's layout as it was before shell profiles became agents, once.
    /// </summary>
    /// <remarks>The same argument as <see cref="BackupBeforeKindMigration"/>, one format later: a tile
    /// layout is the one thing here a user cannot reconstruct from anything else, and turning a terminal
    /// leaf into an agent leaf rewrites what a tile <em>is</em>. It fails soft and never overwrites.
    /// </remarks>
    public void BackupBeforeAgentMigration(string workspaceId) =>
        BackupOnce(workspaceId, PreAgentsBackupSuffix);

    private void BackupOnce(string workspaceId, string suffix)
    {
        var filePath = GetFilePath(workspaceId);
        var backupPath = Path.Combine(_workspacesDir, workspaceId + suffix);
        if (!File.Exists(filePath) || File.Exists(backupPath)) return;

        try
        {
            File.Copy(filePath, backupPath);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not keep a copy of workspace layout '{0}' before migrating it: {1}",
                workspaceId, ex.Message);
        }
    }

    public void SaveLayout(string workspaceId, TileNode? rootTile)
    {
        var state = new WorkspaceState
        {
            WorkspaceId = workspaceId,
            RootTile = rootTile
        };
        var json = JsonSerializer.Serialize(state, JsonDefaults.Options);

        // Through a temporary file and a move: a process killed mid-write — an update restarting it, a
        // crash, a stopped debugger — otherwise left a truncated layout, which the next launch cannot read,
        // replaces with an empty tile and then saves over for good.
        var path = GetFilePath(workspaceId);
        // A name of its own per write: a debounced save and a flush can write the same layout at once.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    public void DebouncedSaveLayout(string workspaceId, Func<TileNode?> getRootTile)
    {
        var pending = new PendingSave(getRootTile);
        lock (_pending)
        {
            if (_pending.Remove(workspaceId, out var previous)) previous.Timer?.Dispose();
            _pending[workspaceId] = pending;
            pending.Timer = new Timer(_ => SaveIfStillPending(workspaceId, pending), null,
                AppDefaults.SaveDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes this layout now if a save of it is waiting, rather than a second from now.</summary>
    /// <remarks>Called before the layout's tiles are disposed — a workspace unloaded, the window closing — so
    /// what is written is the tree as it stood, and nothing waiting is lost with the process.</remarks>
    public void FlushLayout(string workspaceId)
    {
        lock (WriteLockFor(workspaceId))
        {
            PendingSave? pending;
            lock (_pending)
            {
                if (!_pending.Remove(workspaceId, out pending)) return;
                pending.Timer?.Dispose();
            }

            Write(workspaceId, pending);
        }
    }

    private void SaveIfStillPending(string workspaceId, PendingSave pending)
    {
        lock (WriteLockFor(workspaceId))
        {
            lock (_pending)
            {
                // A save replaced or flushed since this timer was set has already been dealt with.
                if (!_pending.TryGetValue(workspaceId, out var current) || current != pending) return;
                _pending.Remove(workspaceId);
                pending.Timer?.Dispose();
            }

            Write(workspaceId, pending);
        }
    }

    private void Write(string workspaceId, PendingSave pending)
    {
        try { SaveLayout(workspaceId, pending.GetRootTile()); }
        catch (Exception ex)
        {
            Trace.TraceWarning("Debounced save failed for workspace '{0}': {1}", workspaceId, ex.Message);
        }
    }

    public void DeleteLayout(string workspaceId)
    {
        var filePath = GetFilePath(workspaceId);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    private string GetFilePath(string workspaceId) =>
        Path.Combine(_workspacesDir, $"{workspaceId}.json");
}
