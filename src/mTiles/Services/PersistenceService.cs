using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

public sealed class PersistenceService
{
    private readonly string _workspacesDir;
    private Timer? _debounceTimer;

    /// <summary>What a copy taken before the tile-kind migration is called.</summary>
    /// <remarks>One suffix rather than a timestamp, unlike <c>settings.bad-…</c>: this is taken once,
    /// on the first launch after the update, and the thing worth keeping is the layout as it was
    /// <em>before</em> the rewrite. A second copy could only be of an already-migrated file, which is
    /// no use to anybody and would overwrite the one that is.</remarks>
    private const string PreKindBackupSuffix = ".pre-kind.json";

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
    public void BackupBeforeKindMigration(string workspaceId)
    {
        var filePath = GetFilePath(workspaceId);
        var backupPath = Path.Combine(_workspacesDir, workspaceId + PreKindBackupSuffix);
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
        File.WriteAllText(GetFilePath(workspaceId), json);
    }

    public void DebouncedSaveLayout(string workspaceId, Func<TileNode?> getRootTile)
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            try { SaveLayout(workspaceId, getRootTile()); }
            catch (Exception ex)
            {
                Trace.TraceWarning("Debounced save failed for workspace '{0}': {1}", workspaceId, ex.Message);
            }
        }, null, AppDefaults.SaveDebounceMs, Timeout.Infinite);
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
