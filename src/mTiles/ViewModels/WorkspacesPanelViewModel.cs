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
    // The directory each watcher stands on is kept with it, because it is the one thing a
    // FileSystemWatcher will not tell anybody when it stops being right (see RefreshAllBranchesAsync).
    private readonly Dictionary<string, (FileSystemWatcher watcher, string gitDir, Timer? debounce)> _headWatchers = new();
    private readonly object _headWatcherLock = new();
    // What `.git/HEAD` said the last time git answered for this row. The evidence the periodic pass
    // judges on, because a watcher cannot be trusted to say it has stopped delivering (see
    // RefreshAllBranchesAsync). Written and read on the UI thread only, like the rows themselves.
    private readonly Dictionary<string, string> _headStamps = new();

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

        var items = workspaceService.Workspaces.Select(CreateItem).ToList();
        items.Sort(DisplayOrder);
        foreach (var item in items)
            Workspaces.Add(item);

        _branchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _branchTimer.Tick += async (_, _) =>
        {
            try { await RefreshAllBranchesAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Branch refresh failed: {0}", ex.Message); }
        };
        _branchTimer.Start();

        _ = RefreshAllBranchesAsync(force: true);

        foreach (var item in Workspaces)
            StartWatchingHead(item);
    }

    private static readonly Comparison<WorkspaceItemViewModel> DisplayOrder = WorkspaceDisplayOrder.Compare;

    private WorkspaceItemViewModel CreateItem(Workspace workspace) =>
        new(workspace) { FavoriteChanged = StoreFavorite };

    private void StoreFavorite(WorkspaceItemViewModel item, bool isFavorite) =>
        _workspaceService.SetFavorite(item.Id, isFavorite);

    /// <summary>Pins a workspace to the top of the list, or unpins it.</summary>
    [RelayCommand]
    private void ToggleFavorite(WorkspaceItemViewModel? item)
    {
        if (item == null) return;
        item.IsFavorite = !item.IsFavorite;
        MoveToDisplayPosition(item);
    }

    /// <summary>Puts a row where the current order says it belongs.</summary>
    /// <remarks>A move, never a remove and re-add: a removal from this collection is how the window
    /// learns a workspace is gone, and it would answer a re-ordering by disposing the workspace's
    /// tiles.</remarks>
    private void MoveToDisplayPosition(WorkspaceItemViewModel item)
    {
        var from = Workspaces.IndexOf(item);
        if (from < 0) return;

        var to = Workspaces.Count(other => !ReferenceEquals(other, item) && DisplayOrder(other, item) < 0);
        if (to != from)
            Workspaces.Move(from, to);
    }

    /// <summary>Where a row belongs in a list it is not in yet.</summary>
    private int FindDisplayIndex(WorkspaceItemViewModel item)
    {
        var index = 0;
        while (index < Workspaces.Count && DisplayOrder(Workspaces[index], item) < 0)
            index++;
        return index;
    }

    /// <summary>
    /// Brings every row's repository state and branch up to date.
    /// </summary>
    /// <param name="force">Ask git for every repository, whatever the row already says. What the first
    /// pass does, and what a row acted on — a repository just created — needs.</param>
    /// <remarks>
    /// <para><b>The periodic pass is a safety net, not the source of the branch.</b> Each repository
    /// has a watcher on its own <c>.git/HEAD</c> (<see cref="StartWatchingHead"/>), and that is what a
    /// checkout in a terminal tile travels by — within the debounce, rather than within thirty seconds.
    /// So the timer's job is the two things a watcher on a file cannot do: notice a repository that has
    /// appeared or gone, and cover a row whose watcher never started or has since failed.</para>
    /// <para><b>Which is why it asks the file system first and git only where the answer changed.</b>
    /// Unforced, this used to spawn one <c>git</c> per row every thirty seconds for the life of the
    /// window — twenty workspaces in the panel, twenty processes, to re-read a branch that had not moved
    /// and that a watcher was already following. <see cref="ResolveGitDir"/> is a <c>Directory.Exists</c>
    /// and at most one small read, so a quiet list now costs no processes at all.</para>
    /// <para>A row that has become a repository gets its watcher here, and one that has stopped being
    /// one loses it: <c>git init</c> in the terminal next door, a clone into an empty folder, a
    /// <c>.git</c> deleted by hand. Nothing else notices those.</para>
    /// </remarks>
    private async Task RefreshAllBranchesAsync(bool force = false)
    {
        var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
        foreach (var item in Workspaces.ToList())
        {
            try
            {
                // Answered from the directory rather than from the branch name, because an empty branch
                // has two causes that must not look alike: a directory with no repository in it, and a
                // repository whose git call failed or has not returned yet.
                var was = item.HasRepository;
                var gitDir = ResolveGitDir(item.DirectoryPath);
                item.HasRepository = gitDir != null;

                if (gitDir == null)
                {
                    // A repository that has gone takes its watcher and its branch with it, or the row
                    // goes on naming a branch of something that is not there. The answer goes too: the
                    // next repository at this path is a different question.
                    StopWatchingHead(item.Id);
                    item.BranchName = "";
                    item.BranchAnswered = false;
                    continue;
                }

                // Started here as well as at construction: a workspace becomes a repository while its
                // row is on screen, and a row without a watcher is one this pass has to keep asking
                // about.
                //
                // Started again, too, when the watcher is standing on a directory that is no longer the
                // repository's. A `.git` file can be repointed — a worktree moved, a submodule's gitdir
                // replaced, `.git` deleted and remade — and a FileSystemWatcher raises no error for
                // that: it goes on watching the old path in silence, which a pass that only asked
                // "is there an entry?" read as watched, and the branch froze for the life of the window.
                if (!SameDirectory(WatchedGitDir(item.Id), gitDir))
                {
                    StopWatchingHead(item.Id);
                    // A different repository at this path is a different question, never mind what the
                    // row already says.
                    item.BranchAnswered = false;
                    StartWatchingHead(item);
                }

                // What decides whether git is asked is HEAD itself, never the presence of a watcher.
                // A FileSystemWatcher on a network share, on \\wsl$ or on some virtual file systems
                // stops delivering without ever raising Error, and a pass that skipped on the entry
                // alone left the row's branch frozen for the life of the window - which is the one
                // thing the old unconditional poll did not do. HEAD is the file the branch name is
                // read out of, so re-reading it answers the whole question, and it is one small read
                // rather than a process: a quiet list still costs no `git` at all.
                //
                // Whether it has been read is a fact of its own and never the emptiness of the name: a
                // detached HEAD, a rebase and a bisect all answer with nothing, and reading that as
                // "still unread" is a git process for that repository on every tick, for ever.
                var stamp = ReadHeadStamp(gitDir);
                if (!force && was == true && item.BranchAnswered
                    && stamp != null && stamp == StampFor(item.Id))
                    continue;

                await ReadBranchIntoRowAsync(item, gitPath, gitDir);
            }
            catch (Exception ex) { Trace.TraceWarning("Branch lookup failed for {0}: {1}", item.DirectoryPath, ex.Message); }
        }
    }

    /// <summary>The directory this row's HEAD watcher stands on, or <c>null</c> if nothing is watching it.</summary>
    private string? WatchedGitDir(string workspaceId)
    {
        lock (_headWatcherLock)
            return _headWatchers.TryGetValue(workspaceId, out var entry) ? entry.gitDir : null;
    }

    /// <summary>Whether two resolved git directories are the same place.</summary>
    /// <remarks>Compared as the paths <see cref="ResolveGitDir"/> hands back — it builds them the same
    /// way every time — case-insensitively where the file system is.</remarks>
    private static bool SameDirectory(string? a, string? b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>What <c>HEAD</c> says right now, or <c>null</c> if it could not be read.</summary>
    /// <remarks>The content rather than a timestamp: it is the ref the branch name is read out of, so
    /// it changes for exactly the checkouts the row has to follow and for nothing else - a HEAD
    /// rewritten with the same ref in it is not a branch change.</remarks>
    private static string? ReadHeadStamp(string gitDir)
    {
        try { return File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim(); }
        catch { return null; }
    }

    /// <summary>What HEAD said the last time git answered for this row.</summary>
    private string? StampFor(string workspaceId) =>
        _headStamps.TryGetValue(workspaceId, out var stamp) ? stamp : null;

    /// <summary>Asks git for this row's branch and records what came back.</summary>
    /// <remarks>
    /// <para>The one place the three askers share - the periodic pass, the watcher's debounce and a
    /// workspace just added - because the sequence has four steps and the copies had already drifted:
    /// the third asked git for a directory with no repository in it, which is a process and a logged
    /// exception every time somebody adds an ordinary folder.</para>
    /// <para>A git that could not be asked answers <c>null</c>, and <c>null</c> is not an answer: the
    /// row keeps what it had and stays unanswered, so the next pass comes back to it. The HEAD stamp is
    /// taken <em>before</em> the question and stored only with an answer, so a checkout landing
    /// mid-call leaves a stamp that no longer matches and is asked about again.</para>
    /// </remarks>
    private async Task ReadBranchIntoRowAsync(WorkspaceItemViewModel item, string gitPath, string? gitDir = null)
    {
        gitDir ??= ResolveGitDir(item.DirectoryPath);
        if (gitDir == null) return;

        var stamp = ReadHeadStamp(gitDir);
        var branch = await GitService.ReadBranchNameAsync(item.DirectoryPath, gitPath);
        if (branch == null) return;

        if (branch != item.BranchName)
            item.BranchName = branch;
        item.BranchAnswered = true;
        if (stamp != null) _headStamps[item.Id] = stamp;
        else _headStamps.Remove(item.Id);
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
        await RefreshAllBranchesAsync(force: true);
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

    /// <summary>
    /// Follows this workspace's <c>.git/HEAD</c>, so a checkout next door reaches the row.
    /// </summary>
    /// <remarks><b>Idempotent</b>, because it is now called from three places — construction, adding a
    /// workspace, and the periodic pass that notices a repository has appeared. Assigning over an
    /// existing entry left the previous <see cref="FileSystemWatcher"/> live, unreferenced and never
    /// disposed: a handle on the directory for the life of the window, and a second notification for
    /// every checkout.</remarks>
    private void StartWatchingHead(WorkspaceItemViewModel item)
    {
        var gitDir = ResolveGitDir(item.DirectoryPath);
        if (gitDir == null) return;
        if (SameDirectory(WatchedGitDir(item.Id), gitDir)) return;

        try
        {
            var watcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                // FileName is load-bearing, not caution. Git does not write HEAD in place: it writes
                // HEAD.lock and renames it over HEAD, which Windows reports as RENAMED_NEW_NAME and not
                // as a write — invisible to LastWrite|Size alone. That used to cost thirty seconds,
                // because the periodic pass asked git anyway; now that the pass skips a watched row, a
                // missed notification is a branch name stale for the life of the window.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) => OnHeadChanged(item.Id);
            // The rename above, and the create for the platforms that delete and rewrite instead. Both
            // land on the same debounce, so a rewrite seen twice is still one git call.
            watcher.Renamed += (_, _) => OnHeadChanged(item.Id);
            watcher.Created += (_, _) => OnHeadChanged(item.Id);
            watcher.Error += (_, e) => OnWatcherFailed(item, e.GetException());

            var duplicate = false;
            (FileSystemWatcher watcher, string gitDir, Timer? debounce)? replaced = null;
            lock (_headWatcherLock)
            {
                // Checked again inside the lock: the periodic pass and the constructor can both be here
                // for the same row, and the loser has to throw its watcher away rather than store it.
                // A watcher on some other directory is the stale one and is what gets thrown away.
                if (_headWatchers.TryGetValue(item.Id, out var existing))
                {
                    if (SameDirectory(existing.gitDir, gitDir)) duplicate = true;
                    else replaced = existing;
                }
                if (!duplicate) _headWatchers[item.Id] = (watcher, gitDir, null);
            }

            if (duplicate)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            if (replaced is { } old)
            {
                old.debounce?.Dispose();
                old.watcher.EnableRaisingEvents = false;
                old.watcher.Dispose();
            }
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
            _headWatchers[workspaceId] = (entry.watcher, entry.gitDir, debounce);
        }
    }

    private async Task RefreshBranchAsync(string workspaceId)
    {
        var item = Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (item == null) return;
        try
        {
            var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
            await ReadBranchIntoRowAsync(item, gitPath);
        }
        catch (Exception ex) { Trace.TraceWarning("Branch refresh failed for {0}: {1}", item.DirectoryPath, ex.Message); }
    }

    /// <summary>Gives up on a watcher that has stopped delivering, so the next pass can start a fresh one.</summary>
    /// <remarks>
    /// <para>A logged error left the entry in place, and an entry is what <see cref="WatchedGitDir"/>
    /// answers on: the periodic pass then skipped the row as watched while nothing was watching it, and
    /// the branch stayed frozen for the life of the window. A buffer overflow, a network drive dropping
    /// and <c>rm -rf .git</c> followed by <c>git init</c> all end here.</para>
    /// <para>Removed on the UI thread, which is where every other change to the watcher table is made,
    /// and never inside the watcher's own callback.</para>
    /// </remarks>
    private void OnWatcherFailed(WorkspaceItemViewModel item, Exception error)
    {
        Trace.TraceWarning("HEAD watcher error for {0}: {1}", item.DirectoryPath, error.Message);
        Dispatcher.UIThread.Post(() =>
        {
            StopWatchingHead(item.Id);
            item.BranchAnswered = false;
        });
    }

    private void StopWatchingHead(string workspaceId)
    {
        (FileSystemWatcher watcher, string gitDir, Timer? debounce) entry;
        lock (_headWatcherLock)
        {
            if (!_headWatchers.Remove(workspaceId, out entry)) return;
        }
        entry.debounce?.Dispose();
        entry.watcher.EnableRaisingEvents = false;
        entry.watcher.Dispose();
        // The stamp belongs to the repository that was being watched, so it goes with it: a reading
        // taken from the old gitdir must not be able to answer for the one that replaces it.
        _headStamps.Remove(workspaceId);
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
        var item = CreateItem(workspace);
        Workspaces.Insert(FindDisplayIndex(item), item);
        SelectedWorkspace = item;

        var gitPath = GitService.ResolveGitPath(_settingsService?.Settings.GitPath);
        // Answered here and not left to the thirty-second refresh: adding a folder that is not a
        // repository is exactly when the offer to create one is worth having, and until this is set the
        // row shows an empty meta line instead — a workspace the user just picked, saying nothing.
        var gitDir = ResolveGitDir(item.DirectoryPath);
        item.HasRepository = gitDir != null;
        try { await ReadBranchIntoRowAsync(item, gitPath, gitDir); }
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

    /// <summary>Closes a workspace's tiles without removing the workspace itself.</summary>
    /// <remarks>Wired from outside, because the tiles belong to the window and not to this list: the
    /// panel knows which row was clicked and nothing at all about what is running in it.</remarks>
    public Func<WorkspaceItemViewModel, Task>? UnloadWorkspace { get; set; }

    /// <summary>Gives a loaded workspace's memory back.</summary>
    /// <remarks>Only a loaded one has anything to give back, so the menu item is dead on every other
    /// row rather than quietly doing nothing.</remarks>
    [RelayCommand(CanExecute = nameof(CanUnloadWorkspace))]
    private Task UnloadWorkspaceAsync(WorkspaceItemViewModel? item) =>
        item != null && UnloadWorkspace is { } unload ? unload(item) : Task.CompletedTask;

    private static bool CanUnloadWorkspace(WorkspaceItemViewModel? item) => item is { IsLoaded: true };

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
