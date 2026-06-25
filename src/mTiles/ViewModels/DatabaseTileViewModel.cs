using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;

namespace mTiles.ViewModels;

public partial class DatabaseTileViewModel : ObservableObject, IDisposable
{
    private readonly string _workingDirectory;
    private readonly SettingsService _settingsService;
    private readonly DatabaseServiceManager _dbManager;

    [ObservableProperty] private string _fontFamily;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private bool _isServiceRunning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _httpPort;
    [ObservableProperty] private int _selectedTab;

    public bool IsConfigTab => SelectedTab == 0;
    public bool IsLogsTab => SelectedTab == 1;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsLogsTab));
    }

    public ObservableCollection<DatabaseItemViewModel> AllDatabases { get; } = [];
    public ObservableCollection<DatabaseItemViewModel> FilteredDatabases { get; } = [];
    public ObservableCollection<DatabaseItemViewModel> WorkspaceDatabases { get; } = [];
    public ObservableCollection<DbLogEntry> LogEntries { get; } = [];

    [ObservableProperty] private string _detectedFilterText = "";
    partial void OnDetectedFilterTextChanged(string value) => ApplyDetectedFilter();
    [RelayCommand] private void ClearDetectedFilter() => DetectedFilterText = "";

    public Action? TileSettingsChanged { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    private List<WorkspaceDatabaseConfig> _workspaceConfigs = [];
    private const int MaxWorkspaceDatabases = 10;

    public DatabaseTileViewModel(string workingDirectory, SettingsService settingsService,
        DatabaseServiceManager dbManager)
    {
        _workingDirectory = workingDirectory;
        _settingsService = settingsService;
        _dbManager = dbManager;

        var s = settingsService.Settings;
        _fontFamily = s.FontFamily;
        _fontSize = s.FontSize;
        _httpPort = s.Database.HttpPort;
        _isServiceRunning = dbManager.IsRunning;

        settingsService.SettingsChanged += OnSettingsChanged;
        dbManager.StateChanged += OnDbStateChanged;
        dbManager.WriteAccessRequested += OnWriteAccessRequested;
        dbManager.Logger.EntryLogged += OnLogEntryLogged;

        LoadWorkspaceConfig();
        RefreshDatabaseList();
        SyncGrants();
    }

    public Action? ScrollLogsToEnd { get; set; }

    private void OnLogEntryLogged(DbLogEntry entry)
    {
        if (entry.Category != "Http") return;
        Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);
            ScrollLogsToEnd?.Invoke();
        });
    }

    private async Task<bool> OnWriteAccessRequested(string databaseKey, string sql)
    {
        if (!_workspaceConfigs.Any(c => c.DatabaseKey.Equals(databaseKey, StringComparison.OrdinalIgnoreCase)))
            return false;

        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var confirm = ConfirmAction;
                if (confirm == null) { tcs.TrySetResult(false); return; }
                var firstWord = sql.TrimStart().Split(' ', 2)[0].ToUpperInvariant();
                var result = await confirm($"Allow {firstWord} on {databaseKey}?\n(write access for 1 min)");
                tcs.TrySetResult(result);
            }
            catch { tcs.TrySetResult(false); }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(false));
        return await tcs.Task;
    }

    private void OnSettingsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var s = _settingsService.Settings;
            if (s.FontFamily != FontFamily) FontFamily = s.FontFamily;
            if (Math.Abs(s.FontSize - FontSize) > AppDefaults.FontSizeEpsilon) FontSize = s.FontSize;
            HttpPort = s.Database.HttpPort;
            IsServiceRunning = _dbManager.IsRunning;
        });
    }

    private void OnDbStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsServiceRunning = _dbManager.IsRunning;
            RefreshDatabaseList();
        });
    }

    private void LoadWorkspaceConfig()
    {
        var configPath = GetWorkspaceConfigPath();
        if (!File.Exists(configPath)) return;
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var cfg = JsonSerializer.Deserialize<WorkspaceDatabaseTileConfig>(json, JsonDefaults.Options);
                if (cfg != null)
                {
                    _workspaceConfigs = cfg.Databases;
                    return;
                }
            }
            _workspaceConfigs = JsonSerializer.Deserialize<List<WorkspaceDatabaseConfig>>(json, JsonDefaults.Options) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Failed to load database workspace config: {ex.Message}");
            _workspaceConfigs = [];
        }
    }

    private void SaveWorkspaceConfig()
    {
        var configPath = GetWorkspaceConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var cfg = new WorkspaceDatabaseTileConfig { Databases = _workspaceConfigs };
        var json = JsonSerializer.Serialize(cfg, JsonDefaults.Options);
        File.WriteAllText(configPath, json);

        SyncGrants();
        _dbManager.UpdateClaudeLocalMd(_workingDirectory, _workspaceConfigs);
        TileSettingsChanged?.Invoke();
    }

    private void SyncGrants()
    {
        _dbManager.RegisterWorkspace(_workingDirectory, _workspaceConfigs);
    }

    private string GetWorkspaceConfigPath() =>
        Path.Combine(_workingDirectory, ".mterminal", "databases.json");

    public void RefreshDatabaseList()
    {
        var wsKeys = new HashSet<string>(_workspaceConfigs.Select(c => c.DatabaseKey), StringComparer.OrdinalIgnoreCase);

        var newEntries = _dbManager.Registry.Entries.OrderBy(e => e.Info.DisplayName).ToList();
        var newKeys = new HashSet<string>(newEntries.Select(e => e.Info.Key), StringComparer.OrdinalIgnoreCase);

        for (int i = AllDatabases.Count - 1; i >= 0; i--)
        {
            if (!newKeys.Contains(AllDatabases[i].Key))
                AllDatabases.RemoveAt(i);
        }

        foreach (var entry in newEntries)
        {
            var isInWorkspace = wsKeys.Contains(entry.Info.Key);
            var existing = AllDatabases.FirstOrDefault(d => d.Key.Equals(entry.Info.Key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.IsInWorkspace = isInWorkspace;
            else
                AllDatabases.Add(new DatabaseItemViewModel(entry.Info, isInWorkspace));
        }

        for (int i = WorkspaceDatabases.Count - 1; i >= 0; i--)
        {
            if (!wsKeys.Contains(WorkspaceDatabases[i].Key))
                WorkspaceDatabases.RemoveAt(i);
        }

        foreach (var config in _workspaceConfigs)
        {
            if (_dbManager.Registry.TryGet(config.DatabaseKey, out var entry) && entry != null)
            {
                var existing = WorkspaceDatabases.FirstOrDefault(d => d.Key.Equals(config.DatabaseKey, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    existing.AllowModifications = config.AllowModifications;
                else
                    WorkspaceDatabases.Add(new DatabaseItemViewModel(entry.Info, true) { AllowModifications = config.AllowModifications });
            }
        }

        StatusText = _dbManager.IsRunning
            ? $"{WorkspaceDatabases.Count}/{_dbManager.Registry.Count} databases"
            : "Databases";

        ApplyDetectedFilter();
    }

    private void ApplyDetectedFilter()
    {
        FilteredDatabases.Clear();
        var filter = DetectedFilterText;
        foreach (var db in AllDatabases)
        {
            if (db.MatchesFilter(filter))
                FilteredDatabases.Add(db);
        }
    }

    public Func<Avalonia.Input.Platform.IClipboard?>? GetClipboard { get; set; }

    [RelayCommand]
    private async Task CopySql(DbLogEntry entry)
    {
        var text = entry.Sql ?? entry.SqlSnippet;
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = GetClipboard?.Invoke();
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    [RelayCommand]
    private async Task CopyError(DbLogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Error)) return;
        var clipboard = GetClipboard?.Invoke();
        if (clipboard != null)
            await clipboard.SetTextAsync(entry.Error);
    }

    [RelayCommand]
    private void SelectTab(string tab)
    {
        if (int.TryParse(tab, out var t))
            SelectedTab = t;
    }

    [RelayCommand]
    private void AddToWorkspace(DatabaseItemViewModel item)
    {
        if (WorkspaceDatabases.Count >= MaxWorkspaceDatabases) return;
        if (_workspaceConfigs.Any(c => c.DatabaseKey.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) return;

        _workspaceConfigs.Add(new WorkspaceDatabaseConfig { DatabaseKey = item.Key });
        SaveWorkspaceConfig();
        RefreshDatabaseList();
    }

    [RelayCommand]
    private void RemoveFromWorkspace(DatabaseItemViewModel item)
    {
        _workspaceConfigs.RemoveAll(c => c.DatabaseKey.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        SaveWorkspaceConfig();
        RefreshDatabaseList();
    }

    [RelayCommand]
    private void ToggleModifications(DatabaseItemViewModel item)
    {
        var config = _workspaceConfigs.FirstOrDefault(c =>
            c.DatabaseKey.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        if (config == null) return;

        config.AllowModifications = !config.AllowModifications;
        item.AllowModifications = config.AllowModifications;
        SaveWorkspaceConfig();
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _dbManager.StateChanged -= OnDbStateChanged;
        _dbManager.WriteAccessRequested -= OnWriteAccessRequested;
        _dbManager.Logger.EntryLogged -= OnLogEntryLogged;
        _dbManager.UnregisterWorkspace(_workingDirectory);
        _dbManager.UpdateClaudeLocalMd(_workingDirectory, []);
    }
}
