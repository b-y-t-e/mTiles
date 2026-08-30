using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Settings out of this machine and back into another one.
/// </summary>
/// <remarks>
/// <para><b>Secrets do not travel.</b> Every field this application encrypts at rest — the provider
/// keys, the database passwords — is written out empty, and that is not caution for its own sake: on
/// Windows they are DPAPI blobs bound to <em>this</em> user on <em>this</em> machine, so a copied one
/// decrypts to nothing anyway, and the alternative is exporting them in plain text into a file the user
/// is about to put in a repository or a chat window. What crosses is the configuration; the keys are
/// typed in again on the other side, once.</para>
/// <para><see cref="ExtraEnv"/> is the documented exception and the reason
/// <see cref="SecretsWarning"/> exists: it is a dictionary the user fills in themselves, this code
/// cannot tell a proxy address from an API token in it, and blanking it would silently break the
/// instances it is there to configure. So it is exported as written, and the user is told before the
/// file is made rather than after.</para>
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

    /// <summary>The file this writes, as a name to offer in the save dialog.</summary>
    public static string SuggestedFileName =>
        $"mtiles-settings-{DateTime.Now:yyyy-MM-dd}.json";

    /// <summary>Writes the settings to <paramref name="path"/>, without the secrets.</summary>
    /// <remarks>Written through <see cref="PrivateFile"/> for the same reason <c>settings.json</c> is:
    /// <see cref="ExtraEnv"/> crosses as the user wrote it, so an export can carry a token, and on Unix
    /// the umask would otherwise leave it readable by everyone on the machine — a save dialog's default
    /// directory is exactly the shared home or temp directory where that matters.</remarks>
    public static void Export(AppSettings settings, string path) =>
        PrivateFile.WriteAllText(path, JsonSerializer.Serialize(WithoutSecrets(settings),
            JsonDefaults.SettingsOptions));

    /// <summary>
    /// The settings in <paramref name="path"/>, or null when nothing usable came out of it.
    /// </summary>
    /// <remarks>Answers rather than throws, and the caller shows the reason: an import is something the
    /// user asked for from a dialog, and a file they picked by hand is exactly where a wrong one is
    /// picked.</remarks>
    public static AppSettings? Import(string path, out string problem)
    {
        problem = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                JsonDefaults.SettingsOptions);
            if (parsed is not null) return parsed;
            problem = "That file is empty.";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        return null;
    }

    /// <summary>A copy with every encrypted field emptied.</summary>
    private static AppSettings WithoutSecrets(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonDefaults.SettingsOptions),
            JsonDefaults.SettingsOptions) ?? new AppSettings();

        foreach (var provider in copy.AiProviderInstances)
            provider.ApiKey = "";

        copy.Database.SqlServer.Password = "";
        copy.Database.PostgreSql.Password = "";
        foreach (var connection in copy.Database.ManualConnections)
            connection.Password = "";

        return copy;
    }
}
