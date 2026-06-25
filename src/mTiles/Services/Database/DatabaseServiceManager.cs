using System.Collections.Concurrent;
using mTiles.Models;

namespace mTiles.Services.Database;

public sealed class DatabaseServiceManager : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DbRegistry _registry;
    private readonly DbLogger _logger;
    private DiscoveryService? _discovery;
    private DbHttpServer? _httpServer;
    private bool _started;

    private readonly ConcurrentDictionary<string, WorkspaceGrant> _workspaceGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _temporaryWriteGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _grantLock = new();
    private Timer? _stateChangedDebounce;

    public DbRegistry Registry => _registry;
    public DbLogger Logger => _logger;
    public bool IsRunning => _started;

    public event Action? StateChanged;
    public event Func<string, string, Task<bool>>? WriteAccessRequested;

    public DatabaseServiceManager(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _registry = new DbRegistry();
        _logger = new DbLogger(Path.Combine(AppPaths.GetAppDataDirectory(), "db-logs"));
        _registry.Changed += OnRegistryChanged;
    }

    public void Start()
    {
        if (_started) return;
        var settings = _settingsService.Settings.Database;
        if (!settings.Enabled) return;

        try
        {
            _httpServer = new DbHttpServer(settings.HttpPort, _registry, _logger, this);
            _httpServer.Start();

            _discovery = new DiscoveryService(_registry, _logger, settings);
            if (settings.SqlServer.Enabled || settings.PostgreSql.Enabled)
                _discovery.Start();

            RegisterManualConnections(settings);

            _started = true;
            _logger.Write("Database service started", "System");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Write($"Failed to start: {ex.Message}", "System");
            Stop();
        }
    }

    public void Stop()
    {
        _discovery?.Stop();
        _discovery?.Dispose();
        _discovery = null;

        _httpServer?.Stop();
        _httpServer?.Dispose();
        _httpServer = null;

        _started = false;
        StateChanged?.Invoke();
    }

    public void Restart()
    {
        Stop();
        Start();
        RegenerateAllClaudeLocalMd();
    }

    private void RegenerateAllClaudeLocalMd()
    {
        var port = _settingsService.Settings.Database.HttpPort;
        foreach (var (workspaceDir, grant) in _workspaceGrants)
            ClaudeLocalMdWriter.Update(workspaceDir, _started ? grant.Databases : [], _registry, port);
    }

    public void RunDiscoveryNow()
    {
        _discovery?.RunNow();
    }

    // -- Workspace grant management --

    public void RegisterWorkspace(string workspaceDir, List<WorkspaceDatabaseConfig> databases)
    {
        lock (_grantLock)
        {
            _workspaceGrants[workspaceDir] = new WorkspaceGrant(databases.ToList());
            RecalculateAllowModifications();
        }
        StateChanged?.Invoke();
    }

    public void UnregisterWorkspace(string workspaceDir)
    {
        lock (_grantLock)
        {
            _workspaceGrants.TryRemove(workspaceDir, out _);
            RecalculateAllowModifications();
        }
        StateChanged?.Invoke();
    }

    public bool IsDatabaseAllowed(string databaseKey)
    {
        foreach (var grant in _workspaceGrants.Values)
        {
            if (grant.Databases.Any(d => d.DatabaseKey.Equals(databaseKey, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public bool IsDatabaseWriteAllowed(string databaseKey)
    {
        if (_temporaryWriteGrants.TryGetValue(databaseKey, out var expires) && DateTime.UtcNow < expires)
            return true;

        foreach (var grant in _workspaceGrants.Values)
        {
            var db = grant.Databases.FirstOrDefault(d =>
                d.DatabaseKey.Equals(databaseKey, StringComparison.OrdinalIgnoreCase));
            if (db is { AllowModifications: true })
                return true;
        }
        return false;
    }

    public async Task<bool> RequestWriteAccessAsync(string databaseKey, string sql)
    {
        var handler = WriteAccessRequested;
        if (handler == null) return false;

        try
        {
            var delegates = handler.GetInvocationList();
            foreach (var d in delegates)
            {
                var fn = (Func<string, string, Task<bool>>)d;
                if (await fn(databaseKey, sql))
                {
                    _temporaryWriteGrants[databaseKey] = DateTime.UtcNow.AddMinutes(1);
                    _logger.Write($"Temporary write access granted for '{databaseKey}' (1 min)", "System");
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void RecalculateAllowModifications()
    {
        foreach (var entry in _registry.Entries)
            entry.Info.AllowModifications = IsDatabaseWriteAllowed(entry.Info.Key);
    }

    public void UpdateClaudeLocalMd(string workspaceDir, List<WorkspaceDatabaseConfig> databases)
    {
        var effectiveDbs = _started && databases.Count > 0 ? databases : [];
        ClaudeLocalMdWriter.Update(workspaceDir, effectiveDbs, _registry, _settingsService.Settings.Database.HttpPort);
    }

    private void RegisterManualConnections(DatabaseSettings settings)
    {
        foreach (var mc in settings.ManualConnections)
        {
            try
            {
                var connStr = BuildConnectionString(mc);
                var info = new DatabaseInstance
                {
                    Server = mc.Server,
                    Instance = mc.Instance,
                    Database = mc.Database,
                    Alias = mc.Alias,
                    Provider = mc.Provider,
                    ConnectionString = connStr,
                    Source = DbSourceType.Manual
                };
                _registry.Register(info);
                _logger.Write($"Registered manual connection: {info.DisplayName}", "System");
            }
            catch (Exception ex)
            {
                _logger.Write($"Failed to register manual connection '{mc.Server}/{mc.Database}': {ex.Message}", "System");
            }
        }
    }

    public static string BuildConnectionString(ManualDatabaseConnection mc)
    {
        if (mc.Provider == DbProviderType.PostgreSQL)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = mc.Server,
                Port = mc.Port > 0 ? mc.Port : 5432,
                Database = mc.Database,
                Timeout = 15
            };
            if (!string.IsNullOrEmpty(mc.Username))
            {
                builder.Username = mc.Username;
                builder.Password = mc.Password;
            }
            return builder.ConnectionString;
        }
        else
        {
            var dataSource = mc.Server;
            if (!string.IsNullOrEmpty(mc.Instance))
                dataSource += $"\\{mc.Instance}";
            if (mc.Port > 0)
                dataSource += $",{mc.Port}";

            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = mc.Database,
                ConnectTimeout = 15,
                Encrypt = false,
                TrustServerCertificate = true
            };
            if (mc.UseIntegratedSecurity || string.IsNullOrEmpty(mc.Username))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = mc.Username;
                builder.Password = mc.Password;
            }
            return builder.ConnectionString;
        }
    }

    private void OnRegistryChanged()
    {
        _stateChangedDebounce?.Dispose();
        _stateChangedDebounce = new Timer(_ => StateChanged?.Invoke(), null, 300, Timeout.Infinite);
    }

    public void Dispose()
    {
        _stateChangedDebounce?.Dispose();
        Stop();
        _logger.Dispose();
    }

    private sealed record WorkspaceGrant(IReadOnlyList<WorkspaceDatabaseConfig> Databases);
}
