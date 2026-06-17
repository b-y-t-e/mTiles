using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
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
    private bool _allChecked = true;

    [ObservableProperty]
    private bool _showDiffPanel = true;

    [ObservableProperty]
    private bool _splitDiff;

    [ObservableProperty]
    private bool _diffTrimIndent = true;

    [ObservableProperty]
    private bool _diffSkipEmptyLines = true;

    [ObservableProperty]
    private CommitLogEntry? _selectedCommit;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _checkSize = 20.0;

    [ObservableProperty]
    private Thickness _itemPadding = new(2, 1);

    public ObservableCollection<GitFileChange> Changes { get; } = [];
    public ObservableCollection<CommitLogEntry> CommitLog { get; } = [];

    public Action? TileSettingsChanged { get; set; }

    private readonly SettingsService? _settingsService;
    private readonly GitService _gitService;
    private readonly GitDirectoryWatcher _watcher;
    private CancellationTokenSource? _refreshCts;

    public GitTileViewModel(string workingDirectory, SettingsService? settingsService = null)
    {
        _worktreePath = workingDirectory;
        _settingsService = settingsService;
        _gitService = new GitService(workingDirectory);
        _watcher = new GitDirectoryWatcher(workingDirectory);
        _watcher.Changed += OnGitDirectoryChanged;

        var s = settingsService?.Settings;
        _fontFamily = s?.FontFamily ?? AppDefaults.FontFamily;
        _fontSize = s?.FontSize ?? AppDefaults.FontSize;
        _diffTrimIndent = s?.DiffTrimIndent ?? true;
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
            Changes.Clear();
            foreach (var change in status.Changes)
                Changes.Add(change);

            if (oldSelected != null)
                SelectedChange = Changes.FirstOrDefault(c => c.FilePath == oldSelected);

            CommitLog.Clear();
            foreach (var entry in status.CommitLog)
                CommitLog.Add(entry);

            StashCount = status.StashCount;

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
        AllChecked = !AllChecked;
        foreach (var change in Changes)
            change.IsChecked = AllChecked;
        CommitCommand.NotifyCanExecuteChanged();
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

    private void OnGitDirectoryChanged()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RefreshAsync(); }
            catch { }
        });
    }

    public void Dispose()
    {
        if (_settingsService != null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        _watcher.Dispose();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
}
