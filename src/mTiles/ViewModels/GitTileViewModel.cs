using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

public partial class GitTileViewModel : ObservableObject, ITileActions
{
    /// <inheritdoc />
    public string KindId => TileKindIds.Git;

    /// <summary>The ids of the three things this tile offers outside its own view.</summary>
    /// <remarks>Three of about twenty commands, and the choice is what a phone can be trusted with:
    /// see what changed, record it, send it. Discard and Undo last commit are the reason
    /// <see cref="TileAction.IsDestructive"/> exists, and they are not offered at all.</remarks>
    public const string RefreshActionId = "refresh";
    public const string CommitActionId = "commit";
    public const string PushActionId = "push";

    /// <inheritdoc />
    public IReadOnlyList<TileAction> Actions =>
    [
        new(RefreshActionId, "Refresh", "refresh", IsEnabled: !IsLoading),
        new(CommitActionId, "Commit", "check", IsEnabled: CanCommit()),
        new(PushActionId, "Push", "upload", IsEnabled: IsGitRepo && !IsLoading),
    ];

    /// <inheritdoc />
    public async Task<TileActionResult> InvokeAsync(string id)
    {
        // Asked again here rather than trusting the snapshot the caller acted on: a phone's copy of the
        // list is as old as the last time anything changed, and a Commit sent against a tile that has
        // since finished committing would run with an empty message.
        if (Actions.FirstOrDefault(a => a.Id == id) is not { } action)
            return TileActionResult.Refused($"This tile has no '{id}'.");

        if (!action.IsEnabled)
            return TileActionResult.Refused($"{action.Label} is not available right now.");

        switch (id)
        {
            case RefreshActionId: await RefreshAsync(); break;
            case CommitActionId: await CommitAsync(); break;
            case PushActionId: await PushAsync(); break;
        }

        return TileActionResult.Ok;
    }

    [ObservableProperty]
    private string _branchName = "";

    [ObservableProperty]
    private string _worktreePath;

    [ObservableProperty]
    private GitFileChange? _selectedChange;

    [ObservableProperty]
    private string _diffText = "";

    [ObservableProperty]
    private string _oldContent = "";

    [ObservableProperty]
    private string _newContent = "";

    [ObservableProperty]
    private string _commitMessage = "";

    [ObservableProperty]
    private string _commitDescription = "";

    [ObservableProperty]
    private bool _showHistory;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _stashCount;

    [ObservableProperty]
    private bool _isGitRepo = true;

    [ObservableProperty]
    private bool _allChecked;

    [ObservableProperty]
    private bool _showDiffPanel = true;

    [ObservableProperty]
    private bool _splitDiff;

    [ObservableProperty]
    private bool _diffTrimIndent = true;

    /// <summary>Mirrors the application setting so the refresh can read it without reaching into the
    /// settings service. A plain field: nothing in this tile's view binds to it, and an observable
    /// property that nobody observes is a notification raised on every change for no reader.</summary>
    private bool _gitIgnoreWorkspaceDir = true;

    [ObservableProperty]
    private bool _diffSkipEmptyLines = true;

    [ObservableProperty]
    private CommitLogEntry? _selectedCommit;

    [ObservableProperty]
    private bool _isPushing;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private int _unpushedCount;

    [ObservableProperty]
    private bool _hasRemote;

    [ObservableProperty]
    private bool _canUndoLastCommit;

    [ObservableProperty]
    private bool _showCommitSuggestions;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffFontSize))]
    private double _fontSize;

    public double DiffFontSize => Math.Round(FontSize * 0.8, 1);

    [ObservableProperty]
    private double _checkSize = 20.0;

    [ObservableProperty]
    private Thickness _itemPadding = new(2, 1);

    public ObservableCollection<GitFileChange> Changes { get; } = [];
    public ObservableCollection<CommitLogEntry> CommitLog { get; } = [];
    public ObservableCollection<string> CommitSuggestions { get; } = [];

    public Action? TileSettingsChanged { get; set; }
    public Func<string, string, IEnumerable<string>?, Task<string?>>? PromptInput { get; set; }
    public Func<string, string, Task>? ShowError { get; set; }

    private readonly SettingsService? _settingsService;
    private GitService _gitService;
    private string _resolvedGitPath;
    /// <summary>This tile's place in the workspace's shared watcher.</summary>
    /// <remarks>Shared rather than owned since the Goal tile started following the tree too: two tiles
    /// with a <see cref="GitDirectoryWatcher"/> each is two recursive watches over one working copy.
    /// The tile still computes the ignored directories — it is the only thing here that asks git for
    /// them — and hands them to the watcher through its own subscription, so they leave the union with
    /// it when this tile closes. A watcher was owned outright before kinds handed one over, which is
    /// why it is still built here when nobody does (the tests, and any caller of the two-argument
    /// constructor).</remarks>
    private readonly WorkspaceGitWatcher _watcher;
    private readonly WorkspaceGitWatcher.Subscription _watch;

    /// <summary>Ours to dispose only when nobody handed one in.</summary>
    private readonly bool _ownsWatcher;
    private CancellationTokenSource? _refreshCts;
    private Dictionary<string, (string Status, bool IsChecked, DateTime Mtime)> _previousState = new();
    private bool _batchUpdate;

    public GitTileViewModel(string workingDirectory, SettingsService? settingsService = null,
        WorkspaceGitWatcher? gitWatcher = null)
    {
        _worktreePath = workingDirectory;
        _settingsService = settingsService;
        _resolvedGitPath = GitService.ResolveGitPath(settingsService?.Settings.GitPath);
        _gitService = new GitService(workingDirectory, _resolvedGitPath);
        _ownsWatcher = gitWatcher is null;
        _watcher = gitWatcher ?? new WorkspaceGitWatcher(workingDirectory);
        _watch = _watcher.Subscribe(OnGitDirectoryChanged);

        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;
        _diffTrimIndent = s?.DiffTrimIndent ?? true;
        _gitIgnoreWorkspaceDir = s?.GitIgnoreWorkspaceDir ?? true;
        UpdateSizeMetrics();

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        Dispatcher.UIThread.Post(async () =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { Trace.TraceWarning("GitTile init refresh failed: {0}", ex.Message); }
        });
    }

    /// <summary>The entry this setting is about. A trailing slash because it is a directory, which is
    /// how a person would write it by hand and what git reads as "directory only".</summary>
    private const string WorkspaceIgnoreEntry = WorkspacePaths.DirName + "/";

    /// <summary>The entry written under the application's old name. Removed whenever this runs, so a
    /// repository does not end up ignoring two directories when only one of them exists.</summary>
    private const string LegacyIgnoreEntry = WorkspacePaths.LegacyDirName + "/";

    /// <summary>
    /// Brings the workspace's <c>.gitignore</c> into line with the setting.
    /// <para>Done on every refresh rather than once, because the file is the user's: they may edit it,
    /// revert it, or clone the repository again, and a setting that only took effect the first time
    /// would be a setting that is quietly wrong most of the time.</para>
    /// <para>Failure never reaches the tile. Writing to someone's working copy can fail for entirely
    /// ordinary reasons — a read-only checkout, a file locked by another tool — and none of them are a
    /// reason to stop showing them their changes. It is caught <em>here</em> and not in
    /// <see cref="GitIgnoreFile"/>, which must be free to abandon the write: a read that failed there
    /// looks exactly like an empty file, and appending to that replaces the user's list with ours.</para>
    /// <para>Asynchronous because this runs inside the refresh, on the UI thread, on every single pass —
    /// and reading and writing a file in the user's working copy is not something to do there.</para>
    /// </summary>
    /// <returns>True when the file was actually written, which is the caller's cue that the status it
    /// holds is out of date. False on failure as well: nothing changed, so nothing needs re-reading.</returns>
    private async Task<bool> ApplyWorkspaceIgnoreSettingAsync(CancellationToken ct)
    {
        try
        {
            // First, and this order is the whole of it. The old entry is removed below on the grounds
            // that the directory it names has been moved — but nothing had moved it. WorkspacePaths
            // migrates on first *use*, and the only users are the note, todo, goal and database tiles:
            // a workspace holding a Git tile and none of those still had `.mterminal/` sitting on disk
            // when its ignore line was deleted, which puts its contents in front of the user as
            // untracked files in their own repository. Asking for the path is what performs the move,
            // and it is a pair of Directory.Exists calls once per workspace afterwards.
            WorkspacePaths.Dir(WorktreePath);

            bool changed = _gitIgnoreWorkspaceDir
                ? await GitIgnoreFile.EnsureAsync(WorktreePath, WorkspaceIgnoreEntry, ct)
                : await GitIgnoreFile.RemoveAsync(WorktreePath, WorkspaceIgnoreEntry, ct);

            // Only once the directory it names has actually gone, and the migration above is allowed
            // not to move it: a workspace opened by both versions keeps both directories on purpose,
            // and a move can fail. Removing the entry in either of those cases un-ignores a directory
            // that is still on disk, and this application's own notes and transcripts appear in the
            // user's repository as untracked files — which is the outcome the entry exists to prevent,
            // arriving via the tidying-up.
            //
            // Whichever way the setting is set, though: the line describes a directory this application
            // no longer uses, so leaving it behind for somebody who turned the feature off would be
            // leaving litter with no way to ask for it to go.
            var legacyRemoved = !WorkspacePaths.LegacyDirExists(WorktreePath)
                                && await GitIgnoreFile.RemoveAsync(WorktreePath, LegacyIgnoreEntry, ct);

            // Told apart, because they are different edits and the log is how anybody finds out this
            // application wrote to their repository. Reporting both as "Added .mtiles/" made the line
            // say the opposite of what happened whenever the only change was dropping the old entry.
            if (changed)
                Trace.TraceInformation("{0} {1} in {2}/.gitignore",
                    _gitIgnoreWorkspaceDir ? "Added" : "Removed", WorkspaceIgnoreEntry, WorktreePath);
            if (legacyRemoved)
                Trace.TraceInformation("Removed {0} from {1}/.gitignore", LegacyIgnoreEntry, WorktreePath);

            return changed || legacyRemoved;
        }
        catch (OperationCanceledException)
        {
            throw;      // the refresh was abandoned; that is not a .gitignore failure
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not update .gitignore in {0}: {1}", WorktreePath, ex.Message);
            return false;
        }
    }

    private void OnSettingsChanged()
    {
        var s = _settingsService!.Settings;
        if (s.FontFamily != FontFamily)
            FontFamily = s.FontFamily;
        if (Math.Abs(s.FontSize - FontSize) > AppDefaults.FontSizeEpsilon)
        {
            FontSize = s.FontSize;
            UpdateSizeMetrics();
        }
        var needsRefresh = false;
        if (s.GitIgnoreWorkspaceDir != _gitIgnoreWorkspaceDir)
        {
            _gitIgnoreWorkspaceDir = s.GitIgnoreWorkspaceDir;
            needsRefresh = true;
        }
        var newGitPath = GitService.ResolveGitPath(s.GitPath);
        if (newGitPath != _resolvedGitPath)
        {
            _resolvedGitPath = newGitPath;
            _gitService = new GitService(_worktreePath, newGitPath);
            needsRefresh = true;
        }
        if (needsRefresh)
            Dispatcher.UIThread.Post(async () => { try { await RefreshAsync(); } catch { } });
    }

    private void UpdateSizeMetrics()
    {
        var scale = FontSize / AppDefaults.FontSize;
        CheckSize = FontSize * AppDefaults.CheckSizeRatio;
        ItemPadding = new Thickness(3 * scale, 2 * scale);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var oldCts = _refreshCts;
        oldCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        oldCts?.Dispose();
        var ct = _refreshCts.Token;

        IsLoading = true;
        try
        {
            var status = await _gitService.GetStatusAsync(ct);
            ct.ThrowIfCancellationRequested();

            IsGitRepo = status.IsGitRepo;
            if (!status.IsGitRepo) return;

            // After the repository check, never before it: a workspace that is not a repository has no
            // business acquiring a .gitignore, and doing this first put one in every plain folder
            // somebody opened as a workspace.
            // Re-read the status when the file actually changed — git decides what counts as a change
            // by looking at .gitignore, so the status in hand predates it and would still list the very
            // files the setting exists to hide. That is the uncommon path: on every refresh after the
            // first, the entry is already there and nothing is written or re-read.
            if (await ApplyWorkspaceIgnoreSettingAsync(ct))
            {
                status = await _gitService.GetStatusAsync(ct);
                ct.ThrowIfCancellationRequested();
            }

            BranchName = status.BranchName;

            var oldSelected = SelectedChange?.FilePath;

            // No filtering here any more. The setting used to hide these files in this list, which left
            // them untracked *and* unignored — invisible here and waiting in every other git client the
            // user opens. Now the entry goes in .gitignore and git itself stops reporting them, so what
            // this tile shows is what `git status` shows. Files already committed under .mtiles/ do
            // appear now, and correctly: ignoring something git is already tracking changes nothing.
            ReconcileChanges(status.Changes);

            SelectedChange = (oldSelected != null
                ? Changes.FirstOrDefault(c => c.FilePath == oldSelected)
                : null) ?? Changes.FirstOrDefault();

            CommitLog.Clear();
            foreach (var entry in status.CommitLog)
                CommitLog.Add(entry);

            StashCount = status.StashCount;
            UnpushedCount = status.UnpushedCount;
            HasRemote = status.HasRemote;
            CanUndoLastCommit = status.CommitLog.Count > 0
                && (!status.HasRemote || status.UnpushedCount > 0);

            _watch.UpdateIgnoredDirs(await _gitService.GetIgnoredDirsAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitTile refresh failed: {0}", ex.Message);
            IsGitRepo = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadDiffForSelectedAsync()
    {
        var change = SelectedChange;
        if (change == null)
        {
            DiffText = "";
            OldContent = "";
            NewContent = "";
            return;
        }

        try
        {
            var result = await _gitService.GetDiffAsync(change);
            DiffText = FormatDiff(result.DiffText);
            OldContent = result.OldContent;
            NewContent = result.NewContent;
        }
        catch (Exception ex)
        {
            DiffText = $"Error loading diff: {ex.Message}";
            OldContent = "";
            NewContent = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        var checkedFiles = Changes.Where(c => c.IsChecked).Select(c => c.FilePath).ToList();
        if (checkedFiles.Count == 0 || string.IsNullOrWhiteSpace(CommitMessage)) return;

        IsLoading = true;
        try
        {
            var fullMsg = CommitMessage;
            if (!string.IsNullOrWhiteSpace(CommitDescription))
                fullMsg += "\n\n" + CommitDescription;

            await _gitService.CommitAsync(checkedFiles, fullMsg);

            CommitMessage = "";
            CommitDescription = "";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitTile commit failed: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanCommit() =>
        !string.IsNullOrWhiteSpace(CommitMessage) && Changes.Any(c => c.IsChecked);

    [RelayCommand]
    private async Task StashAsync()
    {
        IsLoading = true;
        try
        {
            await _gitService.StashAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitTile stash failed: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task StashPopAsync()
    {
        IsLoading = true;
        try
        {
            await _gitService.StashPopAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("GitTile stash pop failed: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleHistory() => ShowHistory = !ShowHistory;

    [RelayCommand]
    private void ToggleDiffPanel() => ShowDiffPanel = !ShowDiffPanel;

    partial void OnShowDiffPanelChanged(bool value) => TileSettingsChanged?.Invoke();

    [RelayCommand]
    private void ToggleSplitDiff() => SplitDiff = !SplitDiff;

    [RelayCommand]
    private void ToggleDiffSkipEmptyLines() => DiffSkipEmptyLines = !DiffSkipEmptyLines;

    [RelayCommand]
    private void ToggleAllChecked()
    {
        _batchUpdate = true;
        var newState = !Changes.All(c => c.IsChecked);
        foreach (var change in Changes)
            change.IsChecked = newState;
        _batchUpdate = false;
        SyncAllChecked();
    }

    private void OnFileCheckedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_batchUpdate && e.PropertyName == nameof(GitFileChange.IsChecked))
            SyncAllChecked();
    }

    private void SyncAllChecked()
    {
        AllChecked = Changes.Count > 0 && Changes.All(c => c.IsChecked);
        CommitCommand.NotifyCanExecuteChanged();
    }

    public Func<IClipboard?>? GetClipboard { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    private string GetFullPath(GitFileChange change) =>
        Path.GetFullPath(Path.Combine(_worktreePath, change.FilePath));

    [RelayCommand]
    private void ShowInExplorer(GitFileChange change)
    {
        var fullPath = GetFullPath(change);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", $"-R \"{fullPath}\"") { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start(new ProcessStartInfo("xdg-open", Path.GetDirectoryName(fullPath)!) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task CopyFilename(GitFileChange change)
    {
        var clipboard = GetClipboard?.Invoke();
        if (clipboard == null) return;
        await clipboard.SetTextAsync(Path.GetFileName(change.FilePath));
    }

    [RelayCommand]
    private async Task CopyFolder(GitFileChange change)
    {
        var clipboard = GetClipboard?.Invoke();
        if (clipboard == null) return;
        var dir = Path.GetDirectoryName(GetFullPath(change));
        if (dir != null) await clipboard.SetTextAsync(dir);
    }

    [RelayCommand]
    private async Task CopyFilepath(GitFileChange change)
    {
        var clipboard = GetClipboard?.Invoke();
        if (clipboard == null) return;
        await clipboard.SetTextAsync(GetFullPath(change));
    }

    [RelayCommand]
    private async Task CopyCommitHash(CommitLogEntry? commit)
    {
        if (commit == null) return;
        var clipboard = GetClipboard?.Invoke();
        if (clipboard == null) return;
        await clipboard.SetTextAsync(commit.Hash);
    }

    [RelayCommand]
    private async Task DiscardChanges(object parameter)
    {
        List<GitFileChange> files;
        if (parameter is List<GitFileChange> list)
            files = list;
        else if (parameter is GitFileChange single)
            files = [single];
        else
            return;

        if (ConfirmAction != null)
        {
            var message = files.Count == 1
                ? $"Discard changes to \"{files[0].FilePath}\"?"
                : $"Discard changes to {files.Count} files?";
            if (!await ConfirmAction(message)) return;
        }

        foreach (var file in files)
        {
            if (file.Status == "?")
            {
                var fullPath = GetFullPath(file);
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            else
            {
                await _gitService.DiscardAsync(file.FilePath);
            }
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenInDefaultProgram(GitFileChange change)
    {
        var fullPath = GetFullPath(change);
        if (!File.Exists(fullPath)) return;
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }

    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();

    partial void OnSelectedChangeChanged(GitFileChange? value)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await LoadDiffForSelectedAsync(); }
            catch (Exception ex) { Trace.TraceWarning("GitTile load diff failed: {0}", ex.Message); }
        });
    }

    partial void OnSelectedCommitChanged(CommitLogEntry? value)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await LoadCommitDiffAsync(); }
            catch (Exception ex) { Trace.TraceWarning("GitTile load commit diff failed: {0}", ex.Message); }
        });
    }

    private async Task LoadCommitDiffAsync()
    {
        var commit = SelectedCommit;
        if (commit == null)
        {
            DiffText = "";
            OldContent = "";
            NewContent = "";
            return;
        }

        try
        {
            var diff = await _gitService.GetCommitDiffAsync(commit.Hash);
            DiffText = FormatDiff(diff);
            OldContent = "";
            NewContent = "";
        }
        catch (Exception ex)
        {
            DiffText = $"Error: {ex.Message}";
        }
    }

    private string FormatDiff(string rawDiff) =>
        DiffFormatter.StripHeader(DiffTrimIndent ? DiffFormatter.TrimCommonIndent(rawDiff) : rawDiff);

    private void ReconcileChanges(List<GitFileChange> newChanges)
    {
        var currentState = new Dictionary<string, (string Status, bool IsChecked, DateTime Mtime)>();
        foreach (var c in Changes)
            currentState[c.FilePath] = (c.Status, c.IsChecked, c.SnapshotMtime);

        foreach (var c in Changes)
            c.PropertyChanged -= OnFileCheckedChanged;
        Changes.Clear();

        var isFirstLoad = currentState.Count == 0 && _previousState.Count == 0;
        foreach (var change in newChanges)
        {
            var mtime = GetMtime(change.FilePath);
            if (currentState.TryGetValue(change.FilePath, out var prev) && prev.Status == change.Status && prev.Mtime == mtime)
                change.IsChecked = prev.IsChecked;
            else if (_previousState.TryGetValue(change.FilePath, out var old) && old.Status == change.Status && old.Mtime == mtime)
                change.IsChecked = old.IsChecked;
            else if (!isFirstLoad)
                change.IsChecked = true;

            change.SnapshotMtime = mtime;
            change.PropertyChanged += OnFileCheckedChanged;
            Changes.Add(change);
        }

        SyncAllChecked();
        _previousState = currentState;
    }

    private DateTime GetMtime(string relativePath)
    {
        try
        {
            var fullPath = Path.Combine(_worktreePath, relativePath);
            return File.GetLastWriteTimeUtc(fullPath);
        }
        catch { return DateTime.MinValue; }
    }

    private void OnGitDirectoryChanged()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RefreshAsync(); }
            catch { }
        });
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (IsPushing || string.IsNullOrEmpty(BranchName)) return;
        IsPushing = true;
        try
        {
            await _gitService.PushAsync(BranchName);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Git push failed: {0}", ex.Message);
            if (ShowError != null) await ShowError("Push failed", ex.Message);
        }
        finally { IsPushing = false; }
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (IsFetching) return;
        IsFetching = true;
        try
        {
            await _gitService.FetchAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Git fetch failed: {0}", ex.Message);
            if (ShowError != null) await ShowError("Fetch failed", ex.Message);
        }
        finally { IsFetching = false; }
    }

    [RelayCommand]
    private async Task UndoLastCommitAsync()
    {
        if (!CanUndoLastCommit) return;
        if (ConfirmAction != null && !await ConfirmAction("Undo last commit? Changes will be moved back to staging."))
            return;
        try
        {
            await _gitService.UndoLastCommitAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Git undo commit failed: {0}", ex.Message);
            if (ShowError != null) await ShowError("Undo failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateTagAsync(CommitLogEntry? commit)
    {
        if (commit == null || PromptInput == null) return;
        try
        {
            var recentTags = await _gitService.GetTagListAsync();
            var tagName = await PromptInput("Add tag", $"Tag for {commit.Hash[..Math.Min(7, commit.Hash.Length)]}", recentTags);
            if (string.IsNullOrWhiteSpace(tagName)) return;
            await _gitService.CreateTagAsync(tagName.Trim(), commit.Hash);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Git create tag failed: {0}", ex.Message);
            if (ShowError != null) await ShowError("Tag failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadCommitSuggestionsAsync()
    {
        try
        {
            var messages = await _gitService.GetRecentMessagesAsync();
            CommitSuggestions.Clear();

            var top3 = messages
                .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            var top3Set = new HashSet<string>(top3, StringComparer.OrdinalIgnoreCase);
            var recent = messages
                .Where(m => !top3Set.Contains(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            foreach (var m in top3) CommitSuggestions.Add(m);
            foreach (var m in recent) CommitSuggestions.Add(m);

            ShowCommitSuggestions = CommitSuggestions.Count > 0;
        }
        catch (Exception ex) { Trace.TraceWarning("Git load suggestions failed: {0}", ex.Message); }
    }

    [RelayCommand]
    private void SelectCommitSuggestion(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        CommitMessage = message;
        ShowCommitSuggestions = false;
    }

    public void Dispose()
    {
        foreach (var c in Changes)
            c.PropertyChanged -= OnFileCheckedChanged;
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _watch.Dispose();
        if (_ownsWatcher) _watcher.Dispose();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
}
