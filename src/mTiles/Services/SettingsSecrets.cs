using mTiles.Models;

namespace mTiles.Services;

/// <summary>One secret held in the settings: a stable key naming it, and how to read and write it.</summary>
/// <param name="Key">Stable across machines — built from the owning row's id where there is one — so an
/// export's vault and an import's restore can find the same field on both sides.</param>
public sealed record SettingsSecret(string Key, Func<string> Get, Action<string> Set);

/// <summary>
/// The one list of every field in <see cref="AppSettings"/> that is encrypted at rest.
/// </summary>
/// <remarks>Everything that treats secrets differently from the rest of the configuration — blanking them
/// for an export, carrying them in a <see cref="PassphraseVault"/>, restoring them from one, and keeping
/// this machine's across an import — walks this list rather than naming the fields itself. A field that
/// is added here is therefore handled by all four at once; one that four hand-kept lists each had to
/// remember would leave an encrypted export silently empty the first time one of them forgot.</remarks>
public static class SettingsSecrets
{
    public static IEnumerable<SettingsSecret> In(AppSettings settings)
    {
        foreach (var provider in settings.AiProviderInstances)
            yield return new SettingsSecret($"provider:{provider.Id}",
                () => provider.ApiKey, value => provider.ApiKey = value);

        foreach (var connection in settings.Database.ManualConnections)
            yield return new SettingsSecret($"connection:{connection.Id}",
                () => connection.Password, value => connection.Password = value);

        var sqlServer = settings.Database.SqlServer;
        yield return new SettingsSecret("database:sqlserver",
            () => sqlServer.Password, value => sqlServer.Password = value);

        var postgreSql = settings.Database.PostgreSql;
        yield return new SettingsSecret("database:postgresql",
            () => postgreSql.Password, value => postgreSql.Password = value);
    }

    /// <summary>The non-empty secrets in <paramref name="settings"/>, by key — the first of any repeated
    /// key wins, which is the row a lookup by id would have found.</summary>
    public static Dictionary<string, string> KnownValues(AppSettings settings)
    {
        var known = new Dictionary<string, string>();
        foreach (var secret in In(settings))
        {
            var value = secret.Get();
            if (value.Length > 0) known.TryAdd(secret.Key, value);
        }
        return known;
    }
}
