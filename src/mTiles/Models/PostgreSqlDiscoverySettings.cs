using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

public sealed class PostgreSqlDiscoverySettings
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Password { get; set; } = "";
    /// <summary>
    /// The ports discovery tries, in order.
    /// </summary>
    /// <remarks>
    /// Refuses a null like every other collection in the settings, and this one is not theoretical: it is
    /// walked with a bare <c>foreach</c> by the discovery scan, and — worse — read by
    /// <c>SettingsViewModel</c> while the main window is being built. A <c>"Ports": null</c> in the file
    /// is therefore not a database that fails to be found; it is an application that does not start.
    /// <para>An empty array is a different thing and left alone: it means "scan nothing", which is a
    /// choice, while a null is the absence of one.</para>
    /// </remarks>
    public int[] Ports { get => _ports; set => _ports = value ?? [5432, 5433, 5434]; }
    private int[] _ports = [5432, 5433, 5434];
}
