using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Settings out of this machine and back into another one.
/// </summary>
/// <remarks>
/// <para><b>Secrets do not travel unless a passphrase says they may.</b> With none, every field this
/// application encrypts at rest — the provider keys, the database passwords — is written out empty, and
/// that is not caution for its own sake: on Windows they are DPAPI blobs bound to <em>this</em> user on
/// <em>this</em> machine, so a copied one decrypts to nothing anyway, and the alternative is exporting
/// them in plain text into a file the user is about to put in a repository or a chat window. What
/// crosses is the configuration; the keys are typed in again on the other side, once. With a passphrase
/// the secrets cross too — inside one <see cref="PassphraseVault"/> property beside the settings,
/// encrypted under it: never in plain text, and never as they are stored.</para>
/// <para><see cref="ExtraEnv"/> is the documented exception and the reason both warnings exist
/// (<see cref="SecretsWarning"/>, <see cref="EncryptedSecretsWarning"/>): it is a dictionary the user
/// fills in themselves, this code cannot tell a proxy address from an API token in it, and blanking it
/// would silently break the instances it is there to configure. So it is exported as written, and the
/// user is told before the file is made rather than after.</para>
/// <para>Round-tripped through the serialiser rather than cloned by hand: the copy that gets blanked has
/// to be a copy, or an export would erase the running configuration's own keys — and a hand-written
/// clone is a list of properties that stops being complete the first time somebody adds one.</para>
/// </remarks>
public static class SettingsPortability
{
    /// <summary>What the user is told before a file is written, because afterwards is too late.</summary>
    public const string SecretsWarning =
        "API keys and database passwords are not exported — they are encrypted for this machine and "
        + "would not work anywhere else. Anything you typed into an agent instance's own environment "
        + "variables is exported as written, so check the file before you share it.";

    /// <summary>The same warning for a file whose secrets travel, encrypted under a passphrase.</summary>
    /// <remarks>The counterpart, not the opposite, of <see cref="SecretsWarning"/>: which of the two the
    /// user is shown is decided by the passphrase they typed, so the confirmation never describes a file
    /// other than the one about to be written. The sentence about <c>ExtraEnv</c> is in both, because it
    /// is the one part true either way.</remarks>
    public const string EncryptedSecretsWarning =
        "API keys and database passwords are in this file, encrypted with the passphrase it was "
        + "exported under — anyone who has both the file and that passphrase has every credential in "
        + "it. Anything you typed into an agent instance's own environment variables is exported as "
        + "written, so check the file before you share it.";

    /// <summary>The file this writes, as a name to offer in the save dialog.</summary>
    public static string SuggestedFileName =>
        $"mtiles-settings-{DateTime.Now:yyyy-MM-dd}.json";

    /// <summary>Writes the settings to <paramref name="path"/>, without the secrets.</summary>
    /// <remarks>Written through <see cref="PrivateFile"/> for the same reason <c>settings.json</c> is:
    /// <see cref="ExtraEnv"/> crosses as the user wrote it, so an export can carry a token, and on Unix
    /// the umask would otherwise leave it readable by everyone on the machine — a save dialog's default
    /// directory is exactly the shared home or temp directory where that matters.</remarks>
    /// <param name="passphrase">Empty keeps the old behaviour: the secrets are left out entirely. With
    /// one, they travel encrypted under it — see <see cref="PassphraseVault"/> for why they cannot travel
    /// as they are stored.</param>
    public static void Export(AppSettings settings, string path, string passphrase = "")
    {
        var copy = WithoutSecrets(settings);
        var vault = passphrase.Length > 0 ? BuildVault(settings, passphrase) : null;

        // Serialised as a JSON object and then given one more property, rather than through a wrapper
        // type: the body has to stay an AppSettings exactly as it was, so that a file written here opens
        // in a build that knows nothing about vaults, and a file written by an older build opens here.
        // An unknown property is ignored by both, which is the whole of the compatibility.
        var body = JsonSerializer.SerializeToNode(copy, JsonDefaults.SettingsOptions)?.AsObject()
                   ?? new JsonObject();
        if (vault is not null)
            body[VaultProperty] = JsonSerializer.SerializeToNode(vault, JsonDefaults.SettingsOptions);

        PrivateFile.WriteAllText(path, body.ToJsonString(JsonDefaults.SettingsOptions));
    }

    /// <summary>
    /// The settings in <paramref name="path"/>, or null when nothing usable came out of it.
    /// </summary>
    /// <remarks>Answers rather than throws, and the caller shows the reason: an import is something the
    /// user asked for from a dialog, and a file they picked by hand is exactly where a wrong one is
    /// picked.</remarks>
    public static AppSettings? Import(string path, out string problem) =>
        Import(path, "", out problem, out _);

    /// <summary>
    /// The settings in <paramref name="path"/>, with their secrets when <paramref name="passphrase"/>
    /// opens them.
    /// </summary>
    /// <param name="needsPassphrase">True when the file carries secrets and the passphrase did not open
    /// them — the caller's cue to ask for one and come back, rather than to import a file with every
    /// credential silently missing.</param>
    public static AppSettings? Import(string path, string passphrase, out string problem,
        out bool needsPassphrase)
    {
        problem = "";
        needsPassphrase = false;

        try
        {
            var text = File.ReadAllText(path);
            if (IsConnectionsFile(text))
            {
                problem = "That is a connections file, not a settings export. It is imported from the "
                          + "Database tab instead, where it is merged into the list rather than "
                          + "replacing the settings.";
                return null;
            }

            var parsed = JsonSerializer.Deserialize<AppSettings>(text, JsonDefaults.SettingsOptions);
            if (parsed is null)
            {
                problem = "That file is empty.";
                return null;
            }

            if (ReadVault(text) is not { } vault) return parsed;

            if (passphrase.Length == 0)
            {
                needsPassphrase = true;
                problem = "This file's passwords and API keys are protected with a passphrase.";
                return null;
            }

            if (PassphraseVault.Open(vault.Protection, passphrase, out problem) is not { } key)
            {
                needsPassphrase = true;
                return null;
            }

            RestoreSecrets(parsed, vault, key);
            return parsed;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> carries secrets that need a passphrase.</summary>
    public static bool IsProtected(string path)
    {
        try
        {
            return ReadVault(File.ReadAllText(path)) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The property the vault is written under, beside the settings rather than inside them.</summary>
    private const string VaultProperty = "SecretVault";

    /// <summary>Whether <paramref name="text"/> is a manual-connections file, named by its Kind.</summary>
    /// <remarks>The other direction of the confusion <see cref="ManualConnectionsBundle.Kind"/> enforces:
    /// every property a connections file carries is one <see cref="AppSettings"/> does not know, so it
    /// deserialises here into the defaults — and importing it would replace the whole dialog with them
    /// while the confirmation read as if the file's own settings were coming in. The two importers are
    /// told apart by name in both directions, which is what keeps either file off the other's route.</remarks>
    private static bool IsConnectionsFile(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(nameof(ManualConnectionsBundle.Kind),
                       out var kind)
                   && kind.ValueKind == JsonValueKind.String
                   && string.Equals(kind.GetString(), ManualConnectionsBundle.FileKind,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static SecretVault? ReadVault(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty(VaultProperty, out var element)) return null;
            var vault = element.Deserialize<SecretVault>(JsonDefaults.SettingsOptions);
            return vault?.Protection.Salt.Length > 0 ? vault : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every secret this export blanks, encrypted under the passphrase.</summary>
    /// <remarks>Keyed by <see cref="SettingsSecrets"/>, the same keys <c>SettingsService.KeepExistingSecrets</c>
    /// matches on, because the two answer one question between them: a secret the file carries wins, and
    /// one it does not carry is filled back in from this machine.</remarks>
    private static SecretVault BuildVault(AppSettings settings, string passphrase)
    {
        var (protection, key) = PassphraseVault.Create(passphrase);
        var vault = new SecretVault { Protection = protection };

        foreach (var (secretKey, value) in SettingsSecrets.KnownValues(settings))
            vault.Secrets[secretKey] = PassphraseVault.Encrypt(value, key);

        return vault;
    }

    /// <summary>Puts the vault's secrets back into the settings that were just read.</summary>
    /// <remarks>A secret that will not decrypt is left empty rather than written as rubbish: empty is
    /// what <c>SettingsService.KeepExistingSecrets</c> reads as "the file said nothing", so the value
    /// already on this machine survives — the safe way round for a key nobody can retype from memory.
    /// </remarks>
    private static void RestoreSecrets(AppSettings settings, SecretVault vault, byte[] key)
    {
        foreach (var secret in SettingsSecrets.In(settings))
            secret.Set(vault.Secrets.TryGetValue(secret.Key, out var cipher)
                ? PassphraseVault.Decrypt(cipher, key) ?? ""
                : "");
    }

    /// <summary>The secrets beside an exported settings file, and how to open them.</summary>
    private sealed class SecretVault
    {
        public ExportProtection Protection { get; set; } = new();
        public Dictionary<string, string> Secrets { get; set; } = [];
    }

    /// <summary>A copy with every encrypted field emptied.</summary>
    private static AppSettings WithoutSecrets(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonDefaults.SettingsOptions),
            JsonDefaults.SettingsOptions) ?? new AppSettings();

        foreach (var secret in SettingsSecrets.In(copy))
            secret.Set("");

        return copy;
    }
}
