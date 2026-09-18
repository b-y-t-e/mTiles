namespace mTiles.Models;

/// <summary>
/// The manual database connections, as a file that can be handed to somebody else.
/// </summary>
/// <remarks>
/// <para><b>Deliberately not <see cref="AppSettings"/>.</b> This travels on its own so that importing a
/// team's connection list cannot touch anything else on the settings dialog, and so that the global
/// import — which is a <em>replacement</em> — and this one — which is a <em>merge</em> — can never be
/// asked of the same file. <see cref="Kind"/> is what enforces that: a settings export dropped into this
/// importer is refused by name rather than deserialised into an empty list and reported as a file with
/// no connections in it.</para>
/// <para><b>Its own DTO rather than <see cref="ManualDatabaseConnection"/>.</b> That model's password
/// carries <c>ProtectedStringConverter</c>, which is DPAPI — bound to one user on one machine, so a blob
/// written into a file for a colleague decrypts to nothing there. The whole point of this format is that
/// the password <em>does</em> cross, under a passphrase the two people share by another route, so the
/// field here is a ciphertext of its own and the model's converter is never involved.</para>
/// </remarks>
public sealed class ManualConnectionsBundle
{
    public const string FileKind = "mtiles-manual-connections";
    public const int CurrentVersion = 1;

    /// <summary>
    /// Empty by default on purpose: a file that does not carry this property is not this format.
    /// </summary>
    /// <remarks>Defaulted to <see cref="FileKind"/> it would be filled in by the deserialiser, and a
    /// settings export — which has no such property — would read back as a perfectly valid connections
    /// file holding no connections. The export writes it explicitly.</remarks>
    public string Kind { get; set; } = "";
    public int Version { get; set; } = CurrentVersion;

    /// <summary>How the passwords in this file are protected, or null when it carries none.</summary>
    public ExportProtection? Protection { get; set; }

    public List<ManualConnectionEntry> Connections { get; set; } = [];
}

/// <summary>One connection as it crosses.</summary>
public sealed class ManualConnectionEntry
{
    public string Id { get; set; } = "";
    public DbProviderType Provider { get; set; } = DbProviderType.SqlServer;
    public string Alias { get; set; } = "";
    public string Server { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Database { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public bool UseIntegratedSecurity { get; set; }

    /// <summary>Base64 of the encrypted password, or empty when this file carries no passwords.</summary>
    public string PasswordCipher { get; set; } = "";

    /// <summary>The one mapping from a stored connection to how it crosses.</summary>
    /// <remarks>Every field but the password: here it is a ciphertext under this file's own passphrase
    /// (<see cref="PasswordCipher"/>), on the model it is DPAPI at rest — two different things with two
    /// different lifetimes, so the caller sets that one explicitly.</remarks>
    public static ManualConnectionEntry From(ManualDatabaseConnection mc) => new()
    {
        Id = mc.Id,
        Provider = mc.Provider,
        Alias = mc.Alias,
        Server = mc.Server,
        Instance = mc.Instance,
        Database = mc.Database,
        Port = mc.Port,
        Username = mc.Username,
        UseIntegratedSecurity = mc.UseIntegratedSecurity,
    };

    /// <summary>The one mapping back, the password left empty for the caller to decrypt into.</summary>
    public ManualDatabaseConnection ToConnection() => new()
    {
        Id = Id,
        Provider = Provider,
        Alias = Alias,
        Server = Server,
        Instance = Instance,
        Database = Database,
        Port = Port,
        Username = Username,
        UseIntegratedSecurity = UseIntegratedSecurity,
    };
}
