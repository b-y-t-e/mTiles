using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

public partial class WorkspacesPanelViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceService _workspaceService;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherTimer _branchTimer;
    private readonly Dictionary<string, (FileSystemWatcher watcher, Timer? debounce)> _headWatchers = new();
    private readonly object _headWatcherLock = new();

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public ObservableCollection<WorkspaceItemViewModel> FilteredWorkspaces { get; } = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showFilter;

    [ObservableProperty]
    private WorkspaceItemViewModel? _selectedWorkspace;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    partial void OnSelectedWorkspaceChanged(WorkspaceItemViewModel? oldValue, WorkspaceItemViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        ApplyFilter();
    }

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    public Func<Task<string?>>? FolderPicker { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }
    public Func<string, string, Task>? ShowError { get; set; }
    public Action? FocusWorkspaceRequested { get; set; }

    public WorkspacesPanelViewModel(WorkspaceService workspaceService, SettingsService? settingsService = null)
    {
        _workspaceService = workspaceService;
        _settingsService = settingsService;

        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        Workspaces.CollectionChanged += (_, _) =>
        {
            ShowFilter = Workspaces.Count > 3;
            if (!ShowFilter) FilterText = string.Empty;
            ApplyFilter();
        };

        foreach (var w in workspaceService.Workspaces.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            Workspaces.Add(new WorkspaceItemViewModel(w));

        _branchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _branchTimer.Tick += async (_, _) =>
        {
            try { await RefreshAllBranchesAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Branch refresh failed: {0}", ex.Message); }
        };
        _branchTimer.Start();

        _ = RefreshAllBranchesAsync();

        foreach (var item in Workspaces)
            StartWatchingHead(item);
    }

    private async Task RefreshAllBranchesAsync()
    {
        var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
        foreach (var item in Workspaces.ToList())
        {
            try
            {
                // Answered from the directory rather than from the branch name, because an empty branch
                // has two causes that must not look alike: a directory with no repository in it, and a
                // repository whose git call failed or has not returned yet.
                item.HasRepository = ResolveGitDir(item.DirectoryPath) != null;

                var branch = await GitService.GetBranchNameAsync(item.DirectoryPath, gitPath);
                if (branch != item.BranchName)
                    item.BranchName = branch;
            }
            catch (Exception ex) { Trace.TraceWarning("Branch lookup failed for {0}: {1}", item.DirectoryPath, ex.Message); }
        }
    }

    /// <summary>Creates a repository in a workspace that has none.</summary>
    /// <remarks>
    /// <para>Asks first. <c>git init</c> writes a directory into somebody's folder, and the offer sits
    /// on a row the user is otherwise clicking to switch workspaces — a mis-click must not leave a
    /// repository behind. An unwired <see cref="ConfirmAction"/> answers no, because writing to a
    /// user's directory is not something to do on the strength of a question nobody was asked.</para>
    /// <para>The refresh afterwards is what replaces the offer with the new branch; without it the row
    /// keeps offering to create the repository that now exists.</para>
    /// </remarks>
    [RelayCommand]
    private async Task CreateRepositoryAsync(WorkspaceItemViewModel? item)
    {
        if (item is not { HasNoRepository: true }) return;
        if (ConfirmAction == null) return;
        if (!await ConfirmAction($"Create a git repository in \"{item.Name}\"?\n\n{item.DirectoryPath}"))
            return;

        try
        {
            var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
            await new GitCommandRunner(item.DirectoryPath, gitPath).RunAsync("init", throwOnError: true);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("git init failed for {0}: {1}", item.DirectoryPath, ex.Message);
            if (ShowError != null)
                await ShowError("Create repository", ex.Message);
            return;
        }

        StartWatchingHead(item);
        await RefreshAllBranchesAsync();
    }

    private static string? ResolveGitDir(string workingDirectory)
    {
        var gitPath = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(gitPath)) return gitPath;
        if (!File.Exists(gitPath)) return null;

        try
        {
            var content = File.ReadAllText(gitPath).Trim();
            if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var target = content["gitdir:".Length..].Trim();
                var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(workingDirectory, target));
                return Directory.Exists(resolved) ? resolved : null;
            }
        }
        catch { }
        return null;
    }

    private void StartWatchingHead(WorkspaceItemViewModel item)
    {
        var gitDir = ResolveGitDir(item.DirectoryPath);
        if (gitDir == null) return;

        try
        {
            var watcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) => OnHeadChanged(item.Id);
            watcher.Error += (_, e) => Trace.TraceWarning("HEAD watcher error for {0}: {1}", item.DirectoryPath, e.GetException().Message);
            lock (_headWatcherLock)
                _headWatchers[item.Id] = (watcher, null);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("HEAD watcher start failed for {0}: {1}", item.DirectoryPath, ex.Message);
        }
    }

    private void OnHeadChanged(string workspaceId)
    {
        lock (_headWatcherLock)
        {
            if (!_headWatchers.TryGetValue(workspaceId, out var entry)) return;
            entry.debounce?.Dispose();
            var debounce = new Timer(_ => Dispatcher.UIThread.Post(() => _ = RefreshBranchAsync(workspaceId)),
                null, AppDefaults.WatcherDebounceMs, Timeout.Infinite);
            _headWatchers[workspaceId] = (entry.watcher, debounce);
        }
    }

    private async Task RefreshBranchAsync(string workspaceId)
    {
        var item = Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (item == null) return;
        try
        {
            var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
            var branch = await GitService.GetBranchNameAsync(item.DirectoryPath, gitPath);
            if (branch != item.BranchName)
                item.BranchName = branch;
        }
        catch (Exception ex) { Trace.TraceWarning("Branch refresh failed for {0}: {1}", item.DirectoryPath, ex.Message); }
    }

    private void StopWatchingHead(string workspaceId)
    {
        (FileSystemWatcher watcher, Timer? debounce) entry;
        lock (_headWatcherLock)
        {
            if (!_headWatchers.Remove(workspaceId, out entry)) return;
        }
        entry.debounce?.Dispose();
        entry.watcher.EnableRaisingEvents = false;
        entry.watcher.Dispose();
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.FontFamily != FontFamily)
            FontFamily = s.FontFamily;
        if (Math.Abs(s.FontSize - FontSize) > AppDefaults.FontSizeEpsilon)
            FontSize = s.FontSize;
    }

    [RelayCommand]
    private void SelectWorkspace(WorkspaceItemViewModel item)
    {
        SelectedWorkspace = item;
        FocusWorkspaceRequested?.Invoke();
    }

    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        var path = FolderPicker != null ? await FolderPicker() : null;
        if (string.IsNullOrEmpty(path)) return;

        var workspace = _workspaceService.AddWorkspace(path);
        var item = new WorkspaceItemViewModel(workspace);
        var index = 0;
        while (index < Workspaces.Count && string.Compare(Workspaces[index].Name, item.Name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        Workspaces.Insert(index, item);
        SelectedWorkspace = item;

        var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
        // Answered here and not left to the thirty-second refresh: adding a folder that is not a
        // repository is exactly when the offer to create one is worth having, and until this is set the
        // row shows an empty meta line instead — a workspace the user just picked, saying nothing.
        item.HasRepository = ResolveGitDir(item.DirectoryPath) != null;
        try { item.BranchName = await GitService.GetBranchNameAsync(item.DirectoryPath, gitPath); }
        catch (Exception ex) { Trace.TraceWarning("Branch lookup failed: {0}", ex.Message); }

        StartWatchingHead(item);
    }

    [RelayCommand]
    private void OpenInFileManager(WorkspaceItemViewModel item)
    {
        var path = item.DirectoryPath;
        if (!Directory.Exists(path)) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RemoveWorkspaceAsync(WorkspaceItemViewModel item)
    {
        if (ConfirmAction != null)
        {
            var confirmed = await ConfirmAction($"Remove workspace \"{item.Name}\"?");
            if (!confirmed) return;
        }

        StopWatchingHead(item.Id);
        _workspaceService.RemoveWorkspace(item.Id);
        Workspaces.Remove(item);
        if (SelectedWorkspace == item)
            SelectedWorkspace = Workspaces.FirstOrDefault();
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var tokens = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        FilteredWorkspaces.Clear();
        foreach (var w in Workspaces)
        {
            if (tokens.Length == 0 || w == SelectedWorkspace || MatchesAllTokens(w, tokens))
                FilteredWorkspaces.Add(w);
        }
    }

    private static bool MatchesAllTokens(WorkspaceItemViewModel w, string[] tokens)
    {
        var haystack = $"{w.Name} {w.DirectoryPath}";
        foreach (var token in tokens)
        {
            if (!haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _branchTimer.Stop();
        foreach (var id in _headWatchers.Keys.ToList())
            StopWatchingHead(id);
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
