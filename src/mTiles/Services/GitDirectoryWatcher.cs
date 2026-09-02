using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

public sealed class GitDirectoryWatcher : IDisposable
{
    private FileSystemWatcher? _gitWatcher;
    private FileSystemWatcher? _worktreeWatcher;
    private Timer? _debounce;
    private readonly string _workingDirectory;
    private readonly string _gitDir;
    private HashSet<string> _ignoredDirs = [];

    public event Action? Changed;

    public GitDirectoryWatcher(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        _gitDir = Path.Combine(workingDirectory, ".git");
    }

    public void UpdateIgnoredDirs(HashSet<string> dirs) => _ignoredDirs = dirs;

    /// <summary>Whether <see cref="Start"/> found a repository and is now watching.</summary>
    /// <remarks>Start is a no-op without a <c>.git</c> directory and never retries by itself, so
    /// whoever owns the watcher has to be able to tell "watching" from "there was nothing to watch
    /// yet" — see <see cref="WorkspaceGitWatcher"/>, which polls until this turns true.</remarks>
    public bool IsWatching => _gitWatcher != null;

    public void Start()
    {
        if (_gitWatcher != null) return;

        if (!Directory.Exists(_gitDir)) return;

        try
        {
            _gitWatcher = new FileSystemWatcher(_gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                InternalBufferSize = 51200,
                EnableRaisingEvents = true
            };
            _gitWatcher.Changed += OnChanged;
            _gitWatcher.Created += OnChanged;
            _gitWatcher.Deleted += OnChanged;
            _gitWatcher.Error += OnWatcherError;

            _worktreeWatcher = new FileSystemWatcher(_workingDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 51200,
                EnableRaisingEvents = true
            };
            _worktreeWatcher.Changed += OnWorktreeChanged;
            _worktreeWatcher.Created += OnWorktreeChanged;
            _worktreeWatcher.Deleted += OnWorktreeChanged;
            _worktreeWatcher.Renamed += OnWorktreeRenamed;
            _worktreeWatcher.Error += OnWatcherError;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitDirectoryWatcher start failed: {0}", ex.Message);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Trace.TraceWarning("FileSystemWatcher error: {0}", e.GetException().Message);
        ScheduleDebounce();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name?.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) == true)
            return;

        ScheduleDebounce();
    }

    private void OnWorktreeChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        ScheduleDebounce();
    }

    private void OnWorktreeRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath) && ShouldIgnore(e.OldFullPath)) return;
        ScheduleDebounce();
    }

    private bool ShouldIgnore(string fullPath)
    {
        if (fullPath.StartsWith(_gitDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(_gitDir, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var dir in _ignoredDirs)
        {
            if (fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void ScheduleDebounce()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Changed?.Invoke(), null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _gitWatcher?.Dispose();
        _worktreeWatcher?.Dispose();
        _debounce?.Dispose();
    }
}
