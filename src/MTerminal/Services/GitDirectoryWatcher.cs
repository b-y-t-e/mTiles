using System.Diagnostics;
using MTerminal.Models;

namespace MTerminal.Services;

public sealed class GitDirectoryWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly string _workingDirectory;

    public event Action? Changed;

    public GitDirectoryWatcher(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public void Start()
    {
        if (_watcher != null) return;

        var gitDir = Path.Combine(_workingDirectory, ".git");
        if (!Directory.Exists(gitDir)) return;

        try
        {
            _watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnGitDirChanged;
            _watcher.Created += OnGitDirChanged;
            _watcher.Deleted += OnGitDirChanged;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitDirectoryWatcher start failed: {0}", ex.Message);
        }
    }

    private void OnGitDirChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name?.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) == true)
            return;

        _debounce?.Dispose();
        _debounce = new Timer(_ => Changed?.Invoke(), null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
