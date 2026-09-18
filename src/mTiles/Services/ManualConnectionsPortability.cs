using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The manual database connections out of this machine and into a colleague's.
/// </summary>
/// <remarks>
/// <para><b>A file of its own, not a slice of the settings export.</b> The global one is a
/// <em>replacement</em> of everything on the settings dialog; this is a <em>merge</em> into one list, and
/// the two must not be reachable from the same file — which <see cref="ManualConnectionsBundle.Kind"/>
/// is what enforces, so a settings export picked here by mistake is refused by name rather than read as
/// a file with no connections in it.</para>
/// <para><b>The passwords travel, under a passphrase.</b> That is the whole reason this exists rather
/// than the list being retyped on the other side — see <see cref="PassphraseVault"/> for why DPAPI
/// cannot do it. Without a passphrase the file is written exactly as the settings export is: the
/// configuration, and no secret at all.</para>
/// </remarks>
public static class ManualConnectionsPortability
{
    /// <summary>What the user is told when the file will carry no passwords.</summary>
    public const string NoPassphraseWarning =
        "Without a passphrase the passwords are not exported — the file carries the servers, databases "
        + "and usernames only, and whoever imports it types the passwords in once.";

    public static string SuggestedFileName =>
        $"mtiles-databases-{DateTime.Now:yyyy-MM-dd}.json";

    /// <summary>Writes <paramref name="connections"/> to <paramref name="path"/>.</summary>
    /// <param name="passphrase">Empty writes a file with no passwords in it.</param>
    /// <remarks>Through <see cref="PrivateFile"/> whether or not there is a passphrase: even without the
    /// passwords this names somebody's servers, databases and logins, and a save dialog's default
    /// directory is exactly the shared home or temp directory where the umask matters.</remarks>
    public static void Export(IEnumerable<ManualDatabaseConnection> connections, string path,
        string passphrase)
    {
        var bundle = new ManualConnectionsBundle { Kind = ManualConnectionsBundle.FileKind };
        byte[]? key = null;

        if (passphrase.Length > 0)
        {
            var (protection, derived) = PassphraseVault.Create(passphrase);
            bundle.Protection = protection;
            key = derived;
        }

        foreach (var mc in connections)
        {
            var entry = ManualConnectionEntry.From(mc);
            entry.PasswordCipher = key is null ? "" : PassphraseVault.Encrypt(mc.Password, key);
            bundle.Connections.Add(entry);
        }

        PrivateFile.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonDefaults.SettingsOptions));
    }

    /// <summary>The bundle in <paramref name="path"/>, or null with the reason.</summary>
    /// <remarks>Answers rather than throws: an import is a file the user picked by hand from a dialog,
    /// which is exactly where the wrong file gets picked.</remarks>
    public static ManualConnectionsBundle? Read(string path, out string problem)
    {
        problem = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<ManualConnectionsBundle>(File.ReadAllText(path),
                JsonDefaults.SettingsOptions);

            if (parsed is null)
            {
                problem = "That file is empty.";
                return null;
            }

            if (!string.Equals(parsed.Kind, ManualConnectionsBundle.FileKind, StringComparison.Ordinal))
            {
                problem = "That is not a connections file. A full settings export is imported from the "
                          + "General tab instead.";
                return null;
            }

            if (parsed.Version > ManualConnectionsBundle.CurrentVersion)
            {
                problem = "That file was written by a newer version of mTiles.";
                return null;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The connections in <paramref name="bundle"/>, with their passwords where a passphrase opens them.
    /// </summary>
    /// <remarks>A password that will not decrypt arrives empty rather than as rubbish: the merge reads
    /// empty as <em>not said</em> and keeps whatever is stored, which is the safe way round.</remarks>
    public static List<ManualDatabaseConnection>? Open(ManualConnectionsBundle bundle, string passphrase,
        out string problem)
    {
        problem = "";
        byte[]? key = null;

        if (bundle.Protection is { } protection)
        {
            key = PassphraseVault.Open(protection, passphrase, out problem);
            if (key is null) return null;
        }

        return bundle.Connections.Select(entry =>
        {
            var mc = entry.ToConnection();
            mc.Id = entry.Id.Length > 0 ? entry.Id : Guid.NewGuid().ToString();
            mc.Password = key is null ? "" : PassphraseVault.Decrypt(entry.PasswordCipher, key) ?? "";
            return mc;
        }).ToList();
    }

    /// <summary>Whether this file needs a passphrase before it can be read.</summary>
    public static bool IsProtected(ManualConnectionsBundle bundle) => bundle.Protection is not null;
}
