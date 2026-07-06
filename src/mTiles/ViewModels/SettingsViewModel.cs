using System.Collections.ObjectModel;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;

namespace mTiles.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public static string CustomShellOption => "Custom...";
    public static string[] ColorThemeNames { get; } = TerminalTheme.BuiltIn.Select(t => t.Name).ToArray();
    private static readonly string[] KnownShellNames = ["Git Bash", "PowerShell", "CMD", "bash", "zsh", "fish"];

    private readonly SettingsService _settingsService;
    private readonly DatabaseServiceManager? _dbManager;

    [ObservableProperty]
    private int _selectedTab;

    public bool IsGeneralTab => SelectedTab == 0;
    public bool IsProfilesTab => SelectedTab == 1;
    public bool IsAiToolsTab => SelectedTab == 2;
    public bool IsDatabaseTab => SelectedTab == 3;

    partial void OnSelectedTabChanged(int oldValue, int newValue)
    {
        OnPropertyChanged(nameof(IsGeneralTab));
        OnPropertyChanged(nameof(IsProfilesTab));
        OnPropertyChanged(nameof(IsAiToolsTab));
        OnPropertyChanged(nameof(IsDatabaseTab));
        if (newValue == 1)
            LoadAiToolOptions();
        if (newValue == 2 && !_aiToolsLoaded)
            _ = LoadAiToolsSafeAsync();
        if (newValue == 3)
            RefreshDatabaseSettings();
        if (oldValue == 3 && newValue != 3 && _dbManager != null)
            _dbManager.StateChanged -= OnDbManagerStateChanged;
    }

    private void LoadAiToolOptions()
    {
        var s = _settingsService.Settings;
        var aiTools = AiToolDetector.Detect(s.CustomAiToolPaths, s.CustomAiTools);
        var sorted = aiTools
            .OrderByDescending(t => t.IsInstalled)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

        AiToolOptions.Clear();
        AiToolOptions.Add(new ComboOption("", "(none)", true));
        foreach (var t in sorted)
            AiToolOptions.Add(new ComboOption(t.BinaryName, t.Name, t.IsInstalled));
    }

    [RelayCommand]
    private void SelectTab(int tab) => SelectedTab = tab;

    public static string[] ShellTypeNames { get; } = Enum.GetNames<ShellType>();

    public static readonly FuncValueConverter<DbSourceType, bool> IsManualSourceConverter = new(source =>
        source == DbSourceType.Manual);

    public static readonly FuncValueConverter<string, string> ShellTypeConverter = new(shellName =>
    {
        if (string.IsNullOrEmpty(shellName)) return "";
        var t = ShellDetector.GetTypeByName(shellName);
        return t != ShellType.Other ? $"({t})" : "";
    });

    public ObservableCollection<string> ShellOptions { get; } = [];
    public ObservableCollection<ComboOption> ProfileShellOptions { get; } = [];
    public ObservableCollection<UserShellProfile> ShellProfiles { get; } = [];

    [ObservableProperty]
    private string _colorThemeName;

    [ObservableProperty]
    private string _terminalFontFamily;

    [ObservableProperty]
    private double _terminalFontSize;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string _selectedShell;

    [ObservableProperty]
    private string _customShellPath;

    [ObservableProperty]
    private string _customShellArgs;

    [ObservableProperty]
    private bool _isCustomShell;

    [ObservableProperty]
    private string _customShellType;

    [ObservableProperty]
    private bool _gitHideMTerminalDir;

    [ObservableProperty]
    private string _gitPath;

    [ObservableProperty]
    private string _gitDetectedPath = "";

    [ObservableProperty]
    private string _gitVersion = "";

    [ObservableProperty]
    private bool _gitFound;

    public Func<Task<string?>>? BrowseGitFile { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    private CancellationTokenSource? _gitDetectCts;

    [ObservableProperty]
    private bool _isEditingProfile;

    [ObservableProperty]
    private string _editProfileName = "";

    [ObservableProperty]
    private ComboOption? _editProfileShell;

    [ObservableProperty]
    private string _editProfileScript = "";

    [ObservableProperty]
    private string _editProfileFallbackScript = "";

    [ObservableProperty]
    private ComboOption? _editProfileAiTool;

    public ObservableCollection<ComboOption> AiToolOptions { get; } = [];

    private UserShellProfile? _editingProfile;

    private bool _aiToolsLoaded;

    public ObservableCollection<AiToolViewModel> AiTools { get; } = [];

    [ObservableProperty]
    private bool _isLoadingAiTools;

    public Func<Task<string?>>? BrowseAiToolFile { get; set; }

    [ObservableProperty]
    private bool _isEditingAiTool;

    [ObservableProperty]
    private string _editAiToolName = "";

    [ObservableProperty]
    private string _editAiToolBinary = "";

    [ObservableProperty]
    private string _editAiToolVersionArgs = "--version";

    [ObservableProperty]
    private string _editAiToolPath = "";

    private UserAiTool? _editingAiTool;

    // Database sub-tabs
    [ObservableProperty] private int _dbSubTab;
    public bool IsDbConfigSubTab => DbSubTab == 0;
    public bool IsDbDatabasesSubTab => DbSubTab == 1;
    partial void OnDbSubTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsDbConfigSubTab));
        OnPropertyChanged(nameof(IsDbDatabasesSubTab));
    }
    [RelayCommand]
    private void SelectDbSubTab(int tab) => DbSubTab = tab;

    // Database settings
    [ObservableProperty] private bool _dbEnabled;
    [ObservableProperty] private int _dbHttpPort;
    [ObservableProperty] private bool _dbSqlServerEnabled;
    [ObservableProperty] private bool _dbSqlServerIntegrated;
    [ObservableProperty] private string _dbSqlServerUsername = "";
    [ObservableProperty] private string _dbSqlServerPassword = "";
    [ObservableProperty] private bool _dbPostgreSqlEnabled;
    [ObservableProperty] private string _dbPostgreSqlUsername = "";
    [ObservableProperty] private string _dbPostgreSqlPassword = "";
    [ObservableProperty] private string _dbPostgreSqlPorts = "";
    [ObservableProperty] private int _dbDiscoveryInterval;
    public string? DbPortError => _dbManager is { IsRunning: false, LastError: not null } ? _dbManager.LastError : null;

    // Detected databases (all sources)
    public ObservableCollection<DatabaseItemViewModel> DiscoveredDatabases { get; } = [];
    public ObservableCollection<DatabaseItemViewModel> FilteredDiscoveredDatabases { get; } = [];
    [ObservableProperty] private bool _isDiscoveryRunning;
    [ObservableProperty] private string _dbFilterText = "";
    partial void OnDbFilterTextChanged(string value) => ApplyDbFilter();
    [RelayCommand] private void ClearDbFilter() => DbFilterText = "";

    // Manual connections
    public ObservableCollection<ManualConnectionViewModel> ManualConnections { get; } = [];
    [ObservableProperty] private bool _isEditingManualConnection;
    [ObservableProperty] private DbProviderType _editConnProvider = DbProviderType.SqlServer;
    public bool IsInstanceVisible => EditConnProvider == DbProviderType.SqlServer;
    partial void OnEditConnProviderChanged(DbProviderType value) => OnPropertyChanged(nameof(IsInstanceVisible));
    [ObservableProperty] private string _editConnAlias = "";
    [ObservableProperty] private string _editConnServer = "";
    [ObservableProperty] private string _editConnInstance = "";
    [ObservableProperty] private string _editConnDatabase = "";
    [ObservableProperty] private int _editConnPort;
    [ObservableProperty] private string _editConnUsername = "";
    [ObservableProperty] private string _editConnPassword = "";
    [ObservableProperty] private bool _editConnIntegrated = true;
    [ObservableProperty] private bool _isTestingEditConn;
    [ObservableProperty] private string? _editConnTestResult;
    public static DbProviderType[] DbProviders { get; } = Enum.GetValues<DbProviderType>();
    private ManualDatabaseConnection? _editingConnection;

    public SettingsViewModel(SettingsService settingsService, DatabaseServiceManager? dbManager = null)
    {
        _settingsService = settingsService;
        _dbManager = dbManager;
        var s = settingsService.Settings;
        _colorThemeName = s.ColorThemeName;
        _terminalFontFamily = s.TerminalFontFamily;
        _terminalFontSize = s.TerminalFontSize;
        _fontFamily = s.FontFamily;
        _fontSize = s.FontSize;
        _customShellPath = s.CustomShellPath;
        _customShellArgs = s.CustomShellArgs;
        _customShellType = s.CustomShellType.ToString();
        _gitHideMTerminalDir = s.GitHideMTerminalDir;
        _gitPath = s.GitPath;

        _ = DetectGitAsync();

        var detected = ShellDetector.Detect();
        var detectedNames = new HashSet<string>(detected.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        ProfileShellOptions.Add(new ComboOption("", "(default)", true));
        foreach (var shell in detected)
        {
            ShellOptions.Add(shell.Name);
            ProfileShellOptions.Add(new ComboOption(shell.Name, shell.Name, true));
        }
        foreach (var name in KnownShellNames)
        {
            if (!detectedNames.Contains(name))
                ProfileShellOptions.Add(new ComboOption(name, name, false));
        }
        ShellOptions.Add(CustomShellOption);

        if (!string.IsNullOrEmpty(s.CustomShellPath))
        {
            _selectedShell = CustomShellOption;
            _isCustomShell = true;
        }
        else if (!string.IsNullOrEmpty(s.DefaultShellName) && ShellOptions.Contains(s.DefaultShellName))
        {
            _selectedShell = s.DefaultShellName;
        }
        else
        {
            _selectedShell = ShellOptions.Count > 1 ? ShellOptions[0] : CustomShellOption;
        }

        foreach (var p in s.ShellProfiles)
            ShellProfiles.Add(p);
    }

    partial void OnColorThemeNameChanged(string value) { _settingsService.Settings.ColorThemeName = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontFamilyChanged(string value) { _settingsService.Settings.TerminalFontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontSizeChanged(double value) { _settingsService.Settings.TerminalFontSize = value; _settingsService.NotifyChanged(); }
    partial void OnFontFamilyChanged(string value) { _settingsService.Settings.FontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnFontSizeChanged(double value) { _settingsService.Settings.FontSize = value; _settingsService.NotifyChanged(); }
    partial void OnGitHideMTerminalDirChanged(bool value) { _settingsService.Settings.GitHideMTerminalDir = value; _settingsService.NotifyChanged(); }
    partial void OnGitPathChanged(string value) { _settingsService.Settings.GitPath = value; _settingsService.NotifyChanged(); _ = DetectGitAsync(); }

    [RelayCommand]
    private async Task BrowseGitPathAsync()
    {
        if (BrowseGitFile == null) return;
        var path = await BrowseGitFile();
        if (!string.IsNullOrEmpty(path))
            GitPath = path;
    }

    [RelayCommand]
    private void ResetGitPath()
    {
        GitPath = "";
    }

    [RelayCommand]
    private async Task DetectGitAsync()
    {
        _gitDetectCts?.Cancel();
        _gitDetectCts?.Dispose();
        var cts = _gitDetectCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(300, cts.Token);
            var resolved = await Task.Run(() => GitService.ResolveGitPath(string.IsNullOrEmpty(GitPath) ? null : GitPath), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            GitDetectedPath = resolved;
            var version = await GitService.TestGitAsync(resolved);
            cts.Token.ThrowIfCancellationRequested();
            GitFound = version != null;
            GitVersion = version ?? "Not found";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitFound = false;
            GitVersion = "Not found";
            System.Diagnostics.Trace.TraceWarning("Git detection failed: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenGitDownload()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://git-scm.com") { UseShellExecute = true });
    }

    partial void OnSelectedShellChanged(string value)
    {
        IsCustomShell = value == CustomShellOption;
        if (IsCustomShell)
        {
            _settingsService.Settings.DefaultShellName = "";
        }
        else
        {
            _settingsService.Settings.DefaultShellName = value;
            _settingsService.Settings.CustomShellPath = "";
            _settingsService.Settings.CustomShellArgs = "";
            _settingsService.Settings.CustomShellType = ShellType.Other;
        }
        _settingsService.NotifyChanged();
    }

    partial void OnCustomShellPathChanged(string value) { _settingsService.Settings.CustomShellPath = value; _settingsService.NotifyChanged(); }
    partial void OnCustomShellArgsChanged(string value) { _settingsService.Settings.CustomShellArgs = value; _settingsService.NotifyChanged(); }
    partial void OnCustomShellTypeChanged(string value)
    {
        if (Enum.TryParse<ShellType>(value, out var t))
        {
            _settingsService.Settings.CustomShellType = t;
            _settingsService.NotifyChanged();
        }
    }

    private ComboOption? FindShellOption(string value) =>
        ProfileShellOptions.FirstOrDefault(o => o.Value == value)
        ?? ProfileShellOptions.FirstOrDefault();

    private ComboOption? FindAiToolOption(string? value) =>
        AiToolOptions.FirstOrDefault(o => o.Value == (value ?? ""))
        ?? AiToolOptions.FirstOrDefault();

    [RelayCommand]
    private void AddProfile()
    {
        var defaultShellName = IsCustomShell ? "" : SelectedShell;
        _editingProfile = new UserShellProfile { Name = "New Profile", ShellName = defaultShellName };
        EditProfileName = _editingProfile.Name;
        EditProfileShell = FindShellOption(defaultShellName);
        EditProfileScript = "";
        EditProfileFallbackScript = "";
        EditProfileAiTool = FindAiToolOption(null);
        IsEditingProfile = true;
    }

    [RelayCommand]
    private void EditProfile(UserShellProfile profile)
    {
        _editingProfile = profile;
        EditProfileName = profile.Name;
        EditProfileShell = FindShellOption(profile.ShellName);
        EditProfileScript = profile.StartupScript;
        EditProfileFallbackScript = profile.FallbackScript;
        EditProfileAiTool = FindAiToolOption(profile.RequiredAiToolBinaryName);
        IsEditingProfile = true;
    }

    [RelayCommand]
    private void DeleteProfile(UserShellProfile profile)
    {
        ShellProfiles.Remove(profile);
        _settingsService.Settings.ShellProfiles.Remove(profile);
        if (_editingProfile == profile)
            IsEditingProfile = false;
        _settingsService.NotifyChanged();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (_editingProfile == null) return;

        _editingProfile.Name = EditProfileName;
        _editingProfile.ShellName = EditProfileShell?.Value ?? "";
        _editingProfile.StartupScript = EditProfileScript;
        _editingProfile.FallbackScript = EditProfileFallbackScript;
        _editingProfile.RequiredAiToolBinaryName = string.IsNullOrEmpty(EditProfileAiTool?.Value) ? null : EditProfileAiTool.Value;

        if (!ShellProfiles.Contains(_editingProfile))
        {
            ShellProfiles.Add(_editingProfile);
            _settingsService.Settings.ShellProfiles.Add(_editingProfile);
        }
        else
        {
            var idx = ShellProfiles.IndexOf(_editingProfile);
            ShellProfiles.RemoveAt(idx);
            ShellProfiles.Insert(idx, _editingProfile);
        }

        IsEditingProfile = false;
        _editingProfile = null;
        _settingsService.NotifyChanged();
    }

    [RelayCommand]
    private void CancelEditProfile()
    {
        IsEditingProfile = false;
        _editingProfile = null;
    }

    private async Task LoadAiToolsSafeAsync()
    {
        try { await LoadAiToolsAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Failed to load AI tools: {0}", ex.Message); }
    }

    private async Task LoadAiToolsAsync()
    {
        if (_aiToolsLoaded) return;
        IsLoadingAiTools = true;

        var customPaths = _settingsService.Settings.CustomAiToolPaths;
        var userTools = _settingsService.Settings.CustomAiTools;
        var detected = await Task.Run(() => AiToolDetector.Detect(customPaths, userTools));
        var tools = detected
            .OrderByDescending(t => t.IsInstalled)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var tool in tools)
            {
                var vm = new AiToolViewModel(tool)
                {
                    BrowseFile = BrowseAiToolFile,
                    OnCustomPathSet = (binaryName, path) =>
                    {
                        _settingsService.Settings.CustomAiToolPaths[binaryName] = path;
                        _settingsService.NotifyChanged();
                    },
                    OnDeleteRequested = DeleteAiTool
                };
                AiTools.Add(vm);
            }
            _aiToolsLoaded = true;
            IsLoadingAiTools = false;
        });

        _ = TestAllToolsAsync();
    }

    [RelayCommand]
    private async Task RedetectAiToolsAsync()
    {
        _aiToolsLoaded = false;
        AiTools.Clear();
        await LoadAiToolsAsync();
    }

    [RelayCommand]
    private async Task TestAllToolsAsync()
    {
        var tasks = AiTools.Where(t => t.IsInstalled).Select(vm => vm.TestCommand.ExecuteAsync(null));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void AddAiTool()
    {
        _editingAiTool = new UserAiTool();
        EditAiToolName = "";
        EditAiToolBinary = "";
        EditAiToolVersionArgs = "--version";
        EditAiToolPath = "";
        IsEditingAiTool = true;
    }

    [RelayCommand]
    private void SaveAiTool()
    {
        if (_editingAiTool == null || string.IsNullOrWhiteSpace(EditAiToolName) || string.IsNullOrWhiteSpace(EditAiToolBinary))
            return;

        _editingAiTool.Name = EditAiToolName.Trim();
        _editingAiTool.BinaryName = EditAiToolBinary.Trim();
        _editingAiTool.VersionArgs = EditAiToolVersionArgs.Trim();
        _editingAiTool.CustomPath = EditAiToolPath.Trim();

        if (!_settingsService.Settings.CustomAiTools.Contains(_editingAiTool))
            _settingsService.Settings.CustomAiTools.Add(_editingAiTool);

        _settingsService.NotifyChanged();
        IsEditingAiTool = false;
        _editingAiTool = null;

        _ = ReloadAiToolsAsync();
    }

    [RelayCommand]
    private void CancelEditAiTool()
    {
        IsEditingAiTool = false;
        _editingAiTool = null;
    }

    private void DeleteAiTool(AiToolViewModel vm)
    {
        var id = vm.Tool.UserToolId;
        if (id == null) return;

        _settingsService.Settings.CustomAiTools.RemoveAll(t => t.Id == id);
        AiTools.Remove(vm);
        _settingsService.NotifyChanged();
    }

    private async Task ReloadAiToolsAsync()
    {
        _aiToolsLoaded = false;
        AiTools.Clear();
        await LoadAiToolsAsync();
    }

    private void RefreshDatabaseSettings()
    {
        var db = _settingsService.Settings.Database;
        DbEnabled = db.Enabled;
        DbHttpPort = db.HttpPort;
        DbSqlServerEnabled = db.SqlServer.Enabled;
        DbSqlServerIntegrated = db.SqlServer.UseIntegratedSecurity;
        DbSqlServerUsername = db.SqlServer.Username;
        DbSqlServerPassword = db.SqlServer.Password;
        DbPostgreSqlEnabled = db.PostgreSql.Enabled;
        DbPostgreSqlUsername = db.PostgreSql.Username;
        DbPostgreSqlPassword = db.PostgreSql.Password;
        DbPostgreSqlPorts = string.Join(", ", db.PostgreSql.Ports);
        DbDiscoveryInterval = db.DiscoveryIntervalMinutes;
        LoadManualConnections();
        RefreshDiscoveredDatabases();

        if (_dbManager != null)
        {
            _dbManager.StateChanged -= OnDbManagerStateChanged;
            _dbManager.StateChanged += OnDbManagerStateChanged;
        }
    }

    private void OnDbManagerStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshDiscoveredDatabases();
            OnPropertyChanged(nameof(DbPortError));
        });
    }

    private void RefreshDiscoveredDatabases()
    {
        if (_dbManager == null) return;

        var entries = _dbManager.Registry.Entries
            .OrderBy(e => e.Info.Source)
            .ThenBy(e => e.Info.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DiscoveredDatabases.Clear();
        foreach (var entry in entries)
            DiscoveredDatabases.Add(new DatabaseItemViewModel(entry.Info, false));
        ApplyDbFilter();
    }

    private void ApplyDbFilter()
    {
        FilteredDiscoveredDatabases.Clear();
        var filter = DbFilterText;
        foreach (var db in DiscoveredDatabases)
        {
            if (db.MatchesFilter(filter))
                FilteredDiscoveredDatabases.Add(db);
        }
    }

    [RelayCommand]
    private async Task RunDiscoveryNowAsync()
    {
        if (_dbManager == null || !_dbManager.IsRunning) return;
        IsDiscoveryRunning = true;
        try
        {
            await Task.Run(() => _dbManager.RunDiscoveryNow());
            RefreshDiscoveredDatabases();
        }
        finally
        {
            IsDiscoveryRunning = false;
        }
    }

    [RelayCommand]
    private void SaveDatabaseSettings()
    {
        var db = _settingsService.Settings.Database;
        db.Enabled = DbEnabled;
        db.HttpPort = Math.Clamp(DbHttpPort, 1024, 65535);
        DbHttpPort = db.HttpPort;
        db.SqlServer.Enabled = DbSqlServerEnabled;
        db.SqlServer.UseIntegratedSecurity = DbSqlServerIntegrated;
        db.SqlServer.Username = DbSqlServerUsername;
        db.SqlServer.Password = DbSqlServerPassword;
        db.PostgreSql.Enabled = DbPostgreSqlEnabled;
        db.PostgreSql.Username = DbPostgreSqlUsername;
        db.PostgreSql.Password = DbPostgreSqlPassword;
        db.PostgreSql.Ports = DbPostgreSqlPorts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var p) ? p : 0)
            .Where(p => p is > 0 and <= 65535)
            .ToArray();
        db.DiscoveryIntervalMinutes = DbDiscoveryInterval > 0 ? DbDiscoveryInterval : 30;
        _settingsService.NotifyChanged();
        _dbManager?.Restart();
    }

    // -- Manual connections --

    private void LoadManualConnections()
    {
        ManualConnections.Clear();
        foreach (var mc in _settingsService.Settings.Database.ManualConnections)
            ManualConnections.Add(new ManualConnectionViewModel(mc));
    }

    [RelayCommand]
    private void AddManualConnection()
    {
        _editingConnection = new ManualDatabaseConnection();
        EditConnProvider = DbProviderType.SqlServer;
        EditConnAlias = "";
        EditConnServer = "";
        EditConnInstance = "";
        EditConnDatabase = "";
        EditConnPort = 0;
        EditConnUsername = "";
        EditConnPassword = "";
        EditConnIntegrated = true;
        IsEditingManualConnection = true;
    }

    [RelayCommand]
    private void EditManualConnection(ManualConnectionViewModel vm)
    {
        var mc = _settingsService.Settings.Database.ManualConnections
            .FirstOrDefault(c => c.Id == vm.Id);
        if (mc == null) return;

        _editingConnection = mc;
        EditConnProvider = mc.Provider;
        EditConnAlias = mc.Alias;
        EditConnServer = mc.Server;
        EditConnInstance = mc.Instance;
        EditConnDatabase = mc.Database;
        EditConnPort = mc.Port;
        EditConnUsername = mc.Username;
        EditConnPassword = mc.Password;
        EditConnIntegrated = mc.UseIntegratedSecurity;
        IsEditingManualConnection = true;
    }

    [RelayCommand]
    private void SaveManualConnection()
    {
        if (_editingConnection == null) return;
        if (string.IsNullOrWhiteSpace(EditConnServer) || string.IsNullOrWhiteSpace(EditConnDatabase))
            return;

        _editingConnection.Provider = EditConnProvider;
        _editingConnection.Alias = EditConnAlias.Trim();
        _editingConnection.Server = EditConnServer.Trim();
        _editingConnection.Instance = EditConnInstance.Trim();
        _editingConnection.Database = EditConnDatabase.Trim();
        _editingConnection.Port = Math.Clamp(EditConnPort, 0, 65535);
        _editingConnection.Username = EditConnUsername.Trim();
        _editingConnection.Password = EditConnPassword;
        _editingConnection.UseIntegratedSecurity = EditConnIntegrated;

        var list = _settingsService.Settings.Database.ManualConnections;
        if (!list.Contains(_editingConnection))
            list.Add(_editingConnection);

        _settingsService.NotifyChanged();
        IsEditingManualConnection = false;
        _editingConnection = null;
        LoadManualConnections();
        _dbManager?.Restart();
    }

    [RelayCommand]
    private void CancelEditManualConnection()
    {
        IsEditingManualConnection = false;
        _editingConnection = null;
    }

    [RelayCommand]
    private async Task TestEditConnection()
    {
        if (IsTestingEditConn) return;
        IsTestingEditConn = true;
        EditConnTestResult = null;
        try
        {
            var mc = new ManualDatabaseConnection
            {
                Provider = EditConnProvider,
                Server = EditConnServer,
                Instance = EditConnInstance,
                Database = EditConnDatabase,
                Port = EditConnPort,
                UseIntegratedSecurity = EditConnIntegrated,
                Username = EditConnUsername,
                Password = EditConnPassword
            };
            var connStr = DatabaseServiceManager.BuildConnectionString(mc);
            var result = await Task.Run(() =>
            {
                try
                {
                    var provider = DbRegistry.CreateProvider(mc.Provider, connStr);
                    if (provider == null) return "Unknown provider";
                    using var conn = provider.CreateConnection();
                    conn.Open();
                    return "OK";
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    return msg.Length > 120 ? msg[..120] + "..." : msg;
                }
            });
            EditConnTestResult = result;
        }
        catch (Exception ex)
        {
            EditConnTestResult = ex.Message.Length > 120 ? ex.Message[..120] + "..." : ex.Message;
        }
        finally
        {
            IsTestingEditConn = false;
        }
    }

    [RelayCommand]
    private async Task DeleteManualConnection(ManualConnectionViewModel vm)
    {
        var name = !string.IsNullOrWhiteSpace(vm.Alias) ? vm.Alias : vm.Database;
        if (ConfirmAction != null && !await ConfirmAction($"Delete connection \"{name}\"?"))
            return;
        _settingsService.Settings.Database.ManualConnections.RemoveAll(c => c.Id == vm.Id);
        ManualConnections.Remove(vm);
        _settingsService.NotifyChanged();
        _dbManager?.Restart();
    }

    [RelayCommand]
    private async Task TestManualConnection(ManualConnectionViewModel vm)
    {
        if (vm.IsTesting) return;
        vm.IsTesting = true;
        vm.TestResult = null;
        try
        {
            var mc = _settingsService.Settings.Database.ManualConnections
                .FirstOrDefault(c => c.Id == vm.Id);
            if (mc == null) { vm.TestResult = "Not found"; return; }

            var connStr = DatabaseServiceManager.BuildConnectionString(mc);
            var result = await Task.Run(() =>
            {
                try
                {
                    var provider = DbRegistry.CreateProvider(mc.Provider, connStr);
                    if (provider == null) return "Unknown provider";
                    using var conn = provider.CreateConnection();
                    conn.Open();
                    return "OK";
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    return msg.Length > 100 ? msg[..100] + "..." : msg;
                }
            });
            vm.TestResult = result;
        }
        catch (Exception ex)
        {
            vm.TestResult = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message;
        }
        finally
        {
            vm.IsTesting = false;
        }
    }
}
