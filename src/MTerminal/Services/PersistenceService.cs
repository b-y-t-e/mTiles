using System.Diagnostics;
using System.Text.Json;
using MTerminal.Models;

namespace MTerminal.Services;

public sealed class PersistenceService
{
    private readonly string _workspacesDir;
    private Timer? _debounceTimer;

    public PersistenceService()
    {
        _workspacesDir = AppPaths.GetWorkspacesDirectory();
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
