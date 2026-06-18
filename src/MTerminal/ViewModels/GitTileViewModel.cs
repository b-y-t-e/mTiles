using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class GitTileViewModel : ObservableObject, IDisposable
{
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

    [ObservableProperty]
    private bool _gitHideMTerminalDir = true;

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
    private readonly GitDirectoryWatcher _watcher;
    private CancellationTokenSource? _refreshCts;
    private Dictionary<string, (string Status, bool IsChecked, DateTime Mtime)> _previousState = new();
    private bool _batchUpdate;

    public GitTileViewModel(string workingDirectory, SettingsService? settingsService = null)
    {
        _worktreePath = workingDirectory;
        _settingsService = settingsService;
        _resolvedGitPath = GitService.ResolveGitPath(settingsService?.Settings.GitPath);
        _gitService = new GitService(workingDirectory, _resolvedGitPath);
        _watcher = new GitDirectoryWatcher(workingDirectory);
        _watcher.Changed += OnGitDirectoryChanged;

        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;
        _diffTrimIndent = s?.DiffTrimIndent ?? true;
        _gitHideMTerminalDir = s?.GitHideMTerminalDir ?? true;
        UpdateSizeMetrics();

        if (_settingsService != null)
            _settingsService.SettingsChanged += OnSettingsChanged;

        Dispatcher.UIThread.Post(async () =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { Trace.TraceWarning("GitTile init refresh failed: {0}", ex.Message); }
        });
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
        if (s.GitHideMTerminalDir != GitHideMTerminalDir)
        {
            GitHideMTerminalDir = s.GitHideMTerminalDir;
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

            BranchName = status.BranchName;

            var oldSelected = SelectedChange?.FilePath;

            var changes = GitHideMTerminalDir
                ? status.Changes.Where(c => !c.FilePath.StartsWith(".mterminal/") && !c.FilePath.StartsWith(".mterminal\\")).ToList()
                : status.Changes;

            ReconcileChanges(changes);

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

            _watcher.UpdateIgnoredDirs(await _gitService.GetIgnoredDirsAsync(ct));
            _watcher.Start();
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
        _watcher.Dispose();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
}
