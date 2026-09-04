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

    /// <summary>Where each registered workspace's agent-facing files live, so a service restart can
    /// rewrite them all. Written by <see cref="UpdateDatabaseSkill"/>, which is the only route a
    /// workspace has into this manager anyway.</summary>
    private readonly ConcurrentDictionary<string, WorkspaceAgentFiles> _agentFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _grantLock = new();
    private Timer? _stateChangedDebounce;

    public DbRegistry Registry => _registry;
    public DbLogger Logger => _logger;
    public bool IsRunning => _started;
    public string? LastError { get; private set; }

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
            LastError = null;
            _logger.Write("Database service started", "System");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = DetectPortConflict(settings.HttpPort) ?? ex.Message;
            _logger.Write($"Failed to start: {LastError}", "System");
            Stop();
        }
    }

    public void Stop()
    {
        var wasRunning = _started;

        _discovery?.Stop();
        _discovery?.Dispose();
        _discovery = null;

        _httpServer?.Stop();
        _httpServer?.Dispose();
        _httpServer = null;

        _started = false;
        if (wasRunning)
            LastError = null;
        StateChanged?.Invoke();
    }

    public void Restart()
    {
        Stop();
        Start();
        RegenerateAllDatabaseSkills();
    }

    /// <summary>Rewrites every workspace's database skill after the service has been restarted.
    /// </summary>
    /// <remarks>A service that came back down is the second of the two triggers for the blind delete —
    /// see <see cref="WorkspaceAgentFiles.RemoveSkillEverywhere"/>: no agent may keep a live database
    /// address for a bridge that is not listening.</remarks>
    private void RegenerateAllDatabaseSkills()
    {
        foreach (var (workspaceDir, grant) in _workspaceGrants)
        {
            if (_agentFiles.TryGetValue(workspaceDir, out var agentFiles))
                UpdateDatabaseSkill(agentFiles, _started ? grant.Databases : []);
        }
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

    /// <summary>
    /// Offers this workspace's databases to its agents as a skill, or takes the offer away.
    /// </summary>
    /// <remarks>The two are not symmetric, and that is the point: a skill is written only where an
    /// agent tile in this workspace reads one, while withdrawing it reaches every directory any agent
    /// could ever read. No database was selected, or the service is not running, and nothing may be
    /// left saying otherwise.</remarks>
    public void UpdateDatabaseSkill(WorkspaceAgentFiles agentFiles,
        IReadOnlyList<WorkspaceDatabaseConfig> databases)
    {
        // Remembered so a restart of the service can rewrite every workspace without each tile having
        // to be asked again — the same reason the grants are kept here.
        _agentFiles[agentFiles.WorkspaceDirectory] = agentFiles;

        // The two questions are separate, and reading the second as the first is what cost a tracked
        // .gitignore its marked block on every launch. "Nothing is offered" is a decision — the service
        // is off, or the last database was unticked. "Nothing could be built" is the registry not
        // having answered yet: discovery runs on a timer off the thread pool, while a restored tile
        // asks this from its own constructor, so at startup every discovered database is still unknown.
        var withdrawn = !_started || databases.Count == 0;
        var skill = withdrawn
            ? null
            : DatabaseSkillWriter.Build(databases, _registry, _settingsService.Settings.Database.HttpPort);

        if (skill != null)
            agentFiles.WriteSkill(DatabaseSkillWriter.SkillName, skill);
        else if (withdrawn)
            agentFiles.RemoveSkill(DatabaseSkillWriter.SkillName);
        else
            // The SKILL.md still goes — it would name an address the bridge is not publishing — but the
            // line it put in the user's .gitignore stays, exactly as it does for a closing tile. The
            // registry raises StateChanged once it knows, and the tile asks again.
            agentFiles.ForgetSkill(DatabaseSkillWriter.SkillName);
    }

    /// <summary>
    /// A database tile is closing: its skill goes, and the <c>.gitignore</c> line it added stays.
    /// </summary>
    /// <remarks>Not <see cref="UpdateDatabaseSkill"/> with an empty list, which is what a user
    /// withdrawing access asks for — see <see cref="WorkspaceAgentFiles.ForgetSkill"/>. Closing the
    /// window disposes every tile, so the two must not be the same call: one is a decision, the other
    /// is the end of a session.</remarks>
    public void ForgetDatabaseSkill(WorkspaceAgentFiles agentFiles)
    {
        _agentFiles.TryRemove(agentFiles.WorkspaceDirectory, out _);
        agentFiles.ForgetSkill(DatabaseSkillWriter.SkillName);
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

    private static string? DetectPortConflict(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveTcpListeners();
            if (!listeners.Any(ep => ep.Port == port))
                return null;

            var processName = FindProcessOnPort(port);
            return processName != null
                ? $"Port {port} is used by {processName}"
                : $"Port {port} is already in use";
        }
        catch
        {
            return null;
        }
    }

    private static string? FindProcessOnPort(int port)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", $"-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            var portSuffix = $":{port} ";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains(portSuffix) || !line.Contains("LISTENING")) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid)) continue;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    return p.ProcessName;
                }
                catch { return null; }
            }
        }
        catch { }
        return null;
    }

    private sealed record WorkspaceGrant(IReadOnlyList<WorkspaceDatabaseConfig> Databases);
}
