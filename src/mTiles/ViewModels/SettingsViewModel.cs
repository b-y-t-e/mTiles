using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using mTiles.Services.Shells;

namespace mTiles.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public static string[] ColorThemeNames { get; } = TerminalTheme.BuiltIn.Select(t => t.Name).ToArray();

    private readonly SettingsService _settingsService;
    private readonly DatabaseServiceManager? _dbManager;

    [ObservableProperty]
    private int _selectedTab;

    /// <summary>The hint in an empty API-key or password field, and the warning under it where this
    /// platform cannot encrypt what it stores. Both come from <see cref="SecretStorage"/>, so the field
    /// and the sentence beside it cannot promise different things.</summary>
    public static string SecretFieldHint => SecretStorage.KeyFieldHint;

    public static bool ShowsPlainSecretWarning => SecretStorage.HasWarning;

    public static string SecretStorageWarning => SecretStorage.Warning ?? "";

    /// <summary>Why the export leaves every secret blank — which is true either way, but for a
    /// different reason once there is no encryption to be bound to this machine.</summary>
    public static string ExportSecretsNote => SecretStorage.IsEncrypted
        ? "API keys and database passwords are not exported — they are encrypted for this machine and "
          + "would not work anywhere else. An import keeps the ones already set up here."
        : "API keys and database passwords are not exported — a file meant to be shared must not carry "
          + "them. An import keeps the ones already set up here.";

    public bool IsGeneralTab => SelectedTab == SettingsTabs.General;
    public bool IsDatabaseTab => SelectedTab == SettingsTabs.Database;
    public bool IsSpeechTab => SelectedTab == SettingsTabs.Speech;

    partial void OnSelectedTabChanged(int oldValue, int newValue)
    {
        OnPropertyChanged(nameof(IsGeneralTab));
        OnPropertyChanged(nameof(IsAiTab));
        OnPropertyChanged(nameof(IsDatabaseTab));
        OnPropertyChanged(nameof(IsSpeechTab));
        if (newValue == SettingsTabs.Speech)
            LoadSpeechOptions();
        if (newValue == SettingsTabs.Database)
            RefreshDatabaseSettings();
        if (oldValue == SettingsTabs.Database && newValue != SettingsTabs.Database && _dbManager != null)
            _dbManager.StateChanged -= OnDbManagerStateChanged;
    }

    [RelayCommand]
    private void SelectTab(int tab) => SelectedTab = tab;

    /// <summary>
    /// Opens the third-party notices that ship beside the executable.
    /// </summary>
    /// <remarks>
    /// This is how the attribution reaches the person running the application, which is what the MIT
    /// licences of the ported code and the CC-BY-4.0 of the speech model actually require — a file in a
    /// git repository reaches nobody who installed a build. If it is missing, say so rather than doing
    /// nothing: a packaging step that dropped it would otherwise be invisible.
    /// </remarks>
    [RelayCommand]
    private async Task OpenThirdPartyNoticesAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (FileHelper.OpenFile(path))
            return;

        if (ShowError is null)
            return;

        // Two different failures, and only one of them is ours. "Missing from this installation" is
        // alarming and wrong in the case that is *the Windows default*: a clean machine has nothing
        // registered for .md, so the shell verb fails and the file is sitting right there. Telling
        // somebody their build is incomplete because they have no Markdown editor is worse than useless
        // — it points the investigation at the packaging, which is fine.
        var message = File.Exists(path)
            ? "The notices file could not be opened — this machine has nothing registered for .md "
              + $"files. It is here, and it is plain text:\n\n{path}"
            : $"The notices file is missing from this installation.\n\n{path}";

        await ShowError("Third-party notices", message);
    }

    public static readonly FuncValueConverter<DbSourceType, bool> IsManualSourceConverter = new(source =>
        source == DbSourceType.Manual);

    public ObservableCollection<string> ShellOptions { get; } = [];

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
    private string _selectedShell = "";

    [ObservableProperty]
    private bool _gitIgnoreWorkspaceDir;

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

    /// <summary>Title and message, shown as a dialog. Wired from the view, like everywhere else.</summary>
    public Func<string, string, Task>? ShowError { get; set; }

    private CancellationTokenSource? _gitDetectCts;

    /// <summary>Whether the entry form is open, and the overlay with it.</summary>
    /// <remarks>Four forms share it — the manual database connection, an agent instance, a provider
    /// instance and a sign-in — and only ever one at a time: two of them true would draw two forms
    /// stacked in one overlay.</remarks>
    public bool IsEditingAnything =>
        IsEditingManualConnection || IsEditingAgentInstance || IsEditingProviderInstance
        || IsEditingSignIn;

    /// <summary>Raised when a form opens, so the view can put the caret in it.</summary>
    public event Action? EditingStarted;

    /// <summary>
    /// Opens one of the forms, and only one.
    /// </summary>
    /// <remarks>
    /// They share a single overlay, and two of them true would draw two forms stacked in it. Cheaper
    /// to keep the invariant in one place than to find out from a screenshot that a form has arrived
    /// without it — which is what the AI page's two would otherwise have done.
    /// </remarks>
    private void BeginEditing(ref bool flag)
    {
        // Whatever the agent form had running stops here too. Lowering the flag is not closing it: the
        // model fetch would carry on and land its answer on the form that replaced it, which is the very
        // thing the cancellation was added for. CancelEditing already did this; opening another form did
        // not, and both are ways of leaving.
        LeaveAgentForm();

        IsEditingManualConnection = false;
        IsEditingAgentInstance = false;
        IsEditingProviderInstance = false;
        IsEditingSignIn = false;
        flag = true;
        OnPropertyChanged(nameof(IsEditingManualConnection));
        OnPropertyChanged(nameof(IsEditingAgentInstance));
        OnPropertyChanged(nameof(IsEditingProviderInstance));
        OnPropertyChanged(nameof(IsEditingSignIn));
        OnPropertyChanged(nameof(IsEditingAnything));
        EditingStarted?.Invoke();
    }

    /// <summary>Closes whichever form is open, discarding it — what Escape and the scrim do.</summary>
    public void CancelEditing()
    {
        if (IsEditingManualConnection) CancelEditManualConnectionCommand.Execute(null);
        if (IsEditingAgentInstance) CancelEditAgentInstanceCommand.Execute(null);
        if (IsEditingSignIn) CancelEditSignInCommand.Execute(null);
        if (IsEditingProviderInstance) CancelEditProviderInstanceCommand.Execute(null);
    }

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

    /// <summary>
    /// Whether the database form holds edits that have not been applied yet — what keeps the Save
    /// button on screen.
    /// <para>Saving here is not like the rest of Settings, which persists as you type: it restarts the
    /// database service, so it has to be deliberate. That makes an unapplied edit something the user
    /// can walk away from without noticing, which is exactly what the button being pinned prevents.</para>
    /// </summary>
    [ObservableProperty] private bool _hasUnsavedDatabaseChanges;

    /// <summary>
    /// The form's fields, by name — the same eleven <see cref="DatabaseFormDiffersFromSettings"/>
    /// compares, and deliberately next to it so the two are read and edited as one thing.
    /// <para>A named set rather than "starts with Db", which was wrong in both directions: it swept in
    /// <c>DbSubTab</c>, <c>DbFilterText</c> and <c>DbPortError</c> — none of which anyone saves — and
    /// said nothing about whether a field the comparison reads is actually watched.</para>
    /// </summary>
    private static readonly HashSet<string> DatabaseFormFields =
    [
        nameof(DbEnabled), nameof(DbHttpPort),
        nameof(DbSqlServerEnabled), nameof(DbSqlServerIntegrated),
        nameof(DbSqlServerUsername), nameof(DbSqlServerPassword),
        nameof(DbPostgreSqlEnabled), nameof(DbPostgreSqlUsername),
        nameof(DbPostgreSqlPassword), nameof(DbPostgreSqlPorts),
        nameof(DbDiscoveryInterval),
    ];

    /// <summary>The same set, for a test that drives every field rather than a hand-written list of
    /// them — a list which had quietly missed both password fields.</summary>
    internal static IReadOnlyCollection<string> DatabaseFormFieldNames => DatabaseFormFields;

    /// <summary>Recomputed from one place rather than from a hook on each of the eleven fields.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is not null && DatabaseFormFields.Contains(e.PropertyName))
            HasUnsavedDatabaseChanges = DatabaseFormDiffersFromSettings();
    }

    /// <summary>
    /// Whether the database form has been filled in from the settings yet.
    /// <para>Until it has, every field holds its type's default — an empty username, an empty password,
    /// no ports — and those defaults differ from anything stored, which is not "the user changed
    /// something". Saving in that state is worse than showing the wrong flag: it writes the blanks over
    /// the real credentials. The form is loaded on first entering the tab, so a view model whose tab
    /// nobody opened must be able to say "there is nothing here to compare, and nothing to save".</para>
    /// </summary>
    private bool _databaseFormLoaded;

    /// <summary>Compares what the form holds against what is stored, through the same normalisation
    /// that saving applies — otherwise a port list of "5432," would read as changed for ever, because
    /// saving it produces something the form never spells that way.</summary>
    private bool DatabaseFormDiffersFromSettings()
    {
        if (!_databaseFormLoaded)
            return false;

        var db = _settingsService.Settings.Database;
        return DbEnabled != db.Enabled
            // The raw value, not the clamped one. Comparing after clamping meant a port of 80 against a
            // stored 1024 read as "saved" — the field showing a number that would never be stored, and
            // nothing on screen to say so. Saving writes the clamped value back into the field, so the
            // two agree again afterwards and this settles.
            || DbHttpPort != db.HttpPort
            || DbSqlServerEnabled != db.SqlServer.Enabled
            || DbSqlServerIntegrated != db.SqlServer.UseIntegratedSecurity
            || DbSqlServerUsername != db.SqlServer.Username
            || DbSqlServerPassword != db.SqlServer.Password
            || DbPostgreSqlEnabled != db.PostgreSql.Enabled
            || DbPostgreSqlUsername != db.PostgreSql.Username
            || DbPostgreSqlPassword != db.PostgreSql.Password
            || !ParsePorts(DbPostgreSqlPorts).SequenceEqual(db.PostgreSql.Ports)
            // A port the parser throws away — "99999", "abc" — leaves the list identical to what is
            // stored while the field still shows it. Untidy spacing does not count: a token that
            // survives trimming and then fails to parse is the user having meant something.
            || HasUnusablePorts(DbPostgreSqlPorts)
            || DbDiscoveryInterval != db.DiscoveryIntervalMinutes;
    }

    /// <summary>The port list as the settings hold it. Shared with saving so the two cannot disagree
    /// about what a given piece of text means.</summary>
    private static int[] ParsePorts(string text) => [.. PortTokens(text)
        .Select(s => int.TryParse(s, out var p) ? p : 0)
        .Where(p => p is > 0 and <= 65535)];

    /// <summary>Whether the text names ports that saving would silently drop.</summary>
    private static bool HasUnusablePorts(string text) =>
        PortTokens(text).Any(s => !int.TryParse(s, out var p) || p is <= 0 or > 65535);

    private static string[] PortTokens(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Detected databases (all sources)
    public ObservableCollection<DatabaseItemViewModel> DiscoveredDatabases { get; } = [];
    public ObservableCollection<DatabaseItemViewModel> FilteredDiscoveredDatabases { get; } = [];
    [ObservableProperty] private bool _isDiscoveryRunning;
    [ObservableProperty] private string _dbFilterText = "";
    partial void OnDbFilterTextChanged(string value) => ApplyDbFilter();
    [RelayCommand] private void ClearDbFilter() => DbFilterText = "";

    // Manual connections
    public ObservableCollection<ManualConnectionViewModel> ManualConnections { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAnything))]
    private bool _isEditingManualConnection;
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

    public SettingsViewModel(SettingsService settingsService, DatabaseServiceManager? dbManager = null,
        Services.Speech.DictationService? dictation = null)
    {
        _settingsService = settingsService;
        _dbManager = dbManager;
        InitializeSpeech(dictation);
        InitializePhone();
        var s = settingsService.Settings;
        _colorThemeName = s.ColorThemeName;
        _terminalFontFamily = s.TerminalFontFamily;
        _terminalFontSize = s.TerminalFontSize;
        _fontFamily = s.FontFamily;
        _fontSize = s.FontSize;
        _gitIgnoreWorkspaceDir = s.GitIgnoreWorkspaceDir;
        _gitPath = s.GitPath;

        _ = DetectGitAsync();

        LoadDefaultShell();
        LoadAiInstances();
    }

    /// <summary>Fills the shell list and marks the stored default in it.</summary>
    /// <remarks>The <em>field</em>, for the reason <c>InitializeSpeech</c> gives: the setter writes the
    /// selection back to settings, and this method only ever reads them — which is what lets an import
    /// use it too.</remarks>
    private void LoadDefaultShell()
    {
        var detected = ShellTerminalCatalog.Detect();
        ShellOptions.Clear();
        foreach (var shell in detected)
            ShellOptions.Add(shell.DisplayName);

#pragma warning disable MVVMTK0034
        _selectedShell = ShellTerminalCatalog.ResolveDefault(_settingsService.Settings, detected).DisplayName;
        // The resolver answers with a shell even when nothing was detected, and a selection the list
        // does not contain is a combo box that shows nothing at all — which reads as "no default shell"
        // on the one machine where saying which one is being guessed at matters most.
        if (!ShellOptions.Contains(_selectedShell))
            ShellOptions.Add(_selectedShell);
#pragma warning restore MVVMTK0034
    }

    partial void OnColorThemeNameChanged(string value) { _settingsService.Settings.ColorThemeName = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontFamilyChanged(string value) { _settingsService.Settings.TerminalFontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontSizeChanged(double value) { _settingsService.Settings.TerminalFontSize = value; _settingsService.NotifyChanged(); }
    partial void OnFontFamilyChanged(string value) { _settingsService.Settings.FontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnFontSizeChanged(double value) { _settingsService.Settings.FontSize = value; _settingsService.NotifyChanged(); }
    partial void OnGitIgnoreWorkspaceDirChanged(bool value) { _settingsService.Settings.GitIgnoreWorkspaceDir = value; _settingsService.NotifyChanged(); }
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

    /// <summary>The list shows what a shell is called, and so does the settings file — the display
    /// name rather than the id, for the rollback reason profiles and layouts store one too.</summary>
    partial void OnSelectedShellChanged(string value)
    {
        _settingsService.Settings.DefaultShellName = ShellTerminalCatalog.Find(value)?.DisplayName ?? "";
        _settingsService.NotifyChanged();
    }

    /// <summary>
    /// Called every time the Database tab is opened: refreshes the lists, and reloads the form
    /// <em>only when there is nothing unsaved in it</em>.
    /// <para>That condition is the whole point. Reloading unconditionally threw the user's edits away
    /// the moment they left the tab and came back — so the pinned Save bar stopped working exactly when
    /// someone reacted to it, which is worse than not having it. The lists are a different matter:
    /// they are read-only and nobody is mid-thought in them.</para>
    /// </summary>
    private void RefreshDatabaseSettings()
    {
        if (!HasUnsavedDatabaseChanges)
            LoadDatabaseForm();

        LoadManualConnections();
        RefreshDiscoveredDatabases();

        if (_dbManager != null)
        {
            _dbManager.StateChanged -= OnDbManagerStateChanged;
            _dbManager.StateChanged += OnDbManagerStateChanged;
        }
    }

    /// <summary>
    /// Asks whether the dialog may close, and answers false when the user would rather go back.
    /// <para>The database form is the only thing in Settings that is not already saved — everything else
    /// persists as you type. Its edits do survive the window closing, so nothing is lost either way; the
    /// point is that closing looks like finishing, and a change that restarts a service should not be
    /// left pending by a gesture that means "I'm done here".</para>
    /// <para>Answering yes discards, rather than just closing: leaving the edits in place would bring
    /// the bar back the next time Settings opened, which is not what someone who said "discard" asked
    /// for. Answering no goes to the tab holding them, because "you have unsaved changes" is not much
    /// use without showing which.</para>
    /// </summary>
    public async Task<bool> TryCloseAsync()
    {
        if (!HasUnsavedDatabaseChanges)
            return true;

        // No way to ask is not a reason to trap the user in a dialog they cannot leave.
        if (ConfirmAction is not { } confirm)
            return true;

        if (!await confirm("The database settings have changes that were never applied.\n\n"
                + "Discard them and close?"))
        {
            SelectedTab = SettingsTabs.Database;
            return false;
        }

        LoadDatabaseForm();     // discarded, so the bar goes down with the window
        return true;
    }

    /// <summary>Fills the form from the stored settings, discarding whatever is in it.</summary>
    private void LoadDatabaseForm()
    {
        var db = _settingsService.Settings.Database;
        _databaseFormLoaded = true;
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
        // Refuses rather than writing blanks over real settings. Nothing in the UI can reach this today,
        // and that is exactly the kind of guarantee that stops being true one refactor later.
        if (!_databaseFormLoaded)
            return;

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
        db.PostgreSql.Ports = ParsePorts(DbPostgreSqlPorts);
        // Written back like the port and the interval above: the field then shows exactly what was
        // stored, so a list the parser trimmed down does not sit there looking saved when it is not.
        DbPostgreSqlPorts = string.Join(", ", db.PostgreSql.Ports);
        // Written back into the field like the port above, so what is on screen is what was stored.
        // Without it, an interval of 0 stays 0 in the form while 30 goes to the settings, and the form
        // reads as unsaved for the rest of the session.
        db.DiscoveryIntervalMinutes = DbDiscoveryInterval > 0 ? DbDiscoveryInterval : 30;
        DbDiscoveryInterval = db.DiscoveryIntervalMinutes;
        _settingsService.NotifyChanged();
        _dbManager?.Restart();

        // Recomputed rather than forced to false. With every normalised field written back above, the
        // two are equal by construction and this is the same answer — deliberately so: the guarantee is
        // that the flag reports the form against the settings, not that saving is entitled to declare
        // them equal. A field added later that saving normalises without writing back would break the
        // second claim silently and leave this one correct.
        HasUnsavedDatabaseChanges = DatabaseFormDiffersFromSettings();
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
        BeginEditing(ref _isEditingManualConnection);
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
        BeginEditing(ref _isEditingManualConnection);
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
