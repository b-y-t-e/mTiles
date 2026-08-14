namespace mTiles.Models;

public sealed class DatabaseSettings
{
    public bool Enabled { get; set; }
    public int HttpPort { get; set; } = 18090;

    public SqlServerDiscoverySettings SqlServer
    {
        get => _sqlServer;
        set => _sqlServer = value ?? new();
    }
    private SqlServerDiscoverySettings _sqlServer = new();
    public PostgreSqlDiscoverySettings PostgreSql
    {
        get => _postgreSql;
        set => _postgreSql = value ?? new();
    }
    private PostgreSqlDiscoverySettings _postgreSql = new();

    public int DiscoveryIntervalMinutes { get; set; } = 30;
    public int StaleCycles { get; set; } = 3;

    public List<ManualDatabaseConnection> ManualConnections
    {
        get => _manualConnections;
        set => _manualConnections = value ?? [];
    }
    private List<ManualDatabaseConnection> _manualConnections = [];
}
