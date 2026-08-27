using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

public sealed class WorkspaceService
{
    private readonly string _filePath;
    private List<Workspace> _workspaces = [];

    public IReadOnlyList<Workspace> Workspaces => _workspaces;

    public WorkspaceService() : this(null) { }

    /// <param name="workspacesFilePath">Where the workspace list lives. Defaults to the user's own
    /// file; a test passes a temporary one, because this both reads and writes and no test may edit the
    /// workspaces of whoever is running it. Internal for that reason.</param>
    internal WorkspaceService(string? workspacesFilePath)
    {
        _filePath = workspacesFilePath ?? AppPaths.GetWorkspacesFilePath();
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _workspaces = JsonSerializer.Deserialize<List<Workspace>>(json, JsonDefaults.Options) ?? [];
        }
        catch
        {
            _workspaces = [];
        }

        var fixed_ = false;
        foreach (var w in _workspaces)
        {
            if (!string.IsNullOrEmpty(w.Name)) continue;
            w.Name = Path.GetFileName(w.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                     ?? w.DirectoryPath;
            fixed_ = true;
        }
        if (fixed_) Save();
    }

    public Workspace AddWorkspace(string directoryPath, string? name = null)
    {
        var workspace = new Workspace
        {
            Name = name ?? Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? directoryPath,
            DirectoryPath = directoryPath
        };
        _workspaces.Add(workspace);
        Save();
        return workspace;
    }

    /// <summary>Pins a workspace to the top of the panel, or unpins it.</summary>
    /// <remarks>Written through immediately rather than on a debounce: it is one flag the user has just
    /// clicked, and the thing a pin has to survive is the application being closed straight after.</remarks>
    public void SetFavorite(string workspaceId, bool isFavorite)
    {
        // No "already set, nothing to do" check: the panel's row holds the very same Workspace
        // instance and sets the flag itself, so the two are equal by the time this is called and the
        // shortcut would skip the only thing left to do — writing it down.
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return;
        workspace.IsFavorite = isFavorite;
        Save();
    }

    public void RemoveWorkspace(string workspaceId)
    {
        _workspaces.RemoveAll(w => w.Id == workspaceId);
        Save();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_workspaces, JsonDefaults.Options);
        File.WriteAllText(_filePath, json);
    }
}
