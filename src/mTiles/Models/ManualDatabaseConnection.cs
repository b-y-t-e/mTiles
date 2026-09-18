using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

public sealed class ManualDatabaseConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DbProviderType Provider { get; set; } = DbProviderType.SqlServer;
    public string Alias { get; set; } = "";
    public string Server { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Database { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Password { get; set; } = "";
    public bool UseIntegratedSecurity { get; set; }

    /// <summary>An independent copy: every field, the id and the password included.</summary>
    /// <remarks><see cref="object.MemberwiseClone"/> rather than a hand-kept field list, so a field
    /// added to this model cannot be forgotten here — this is the one clone the merge preview and the
    /// portability both go through, and its whole point is that it cannot drift.</remarks>
    public ManualDatabaseConnection Clone() => (ManualDatabaseConnection)MemberwiseClone();
}
