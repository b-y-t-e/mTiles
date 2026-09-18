using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The settings export's secrets: they cross under a passphrase, and a file written without one is
/// exactly the file this application always wrote.
/// </summary>
public class SettingsVaultTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"mtiles-test-{Guid.NewGuid():N}.json");

    private static AppSettings WithSecrets()
    {
        var settings = new AppSettings();
        settings.AiProviderInstances.Add(new AiProviderInstance
        {
            Id = "p1",
            ProviderId = "openrouter",
            ApiKey = "sk-or-secret",
        });
        settings.Database.ManualConnections.Add(new ManualDatabaseConnection
        {
            Id = "c1",
            Server = "db1",
            Database = "orders",
            Password = "db-secret",
        });
        settings.Database.SqlServer.Password = "sql-secret";
        return settings;
    }

    [Fact]
    public void With_a_passphrase_every_secret_crosses_and_none_of_them_in_the_clear()
    {
        var path = TempFile();
        try
        {
            SettingsPortability.Export(WithSecrets(), path, "correct horse");
            var text = File.ReadAllText(path);

            Assert.DoesNotContain("sk-or-secret", text);
            Assert.DoesNotContain("db-secret", text);
            Assert.DoesNotContain("sql-secret", text);
            Assert.True(SettingsPortability.IsProtected(path));

            var imported = SettingsPortability.Import(path, "correct horse", out var problem, out _);
            Assert.NotNull(imported);
            Assert.Equal("", problem);
            Assert.Equal("sk-or-secret", imported!.AiProviderInstances[0].ApiKey);
            Assert.Equal("db-secret", imported.Database.ManualConnections[0].Password);
            Assert.Equal("sql-secret", imported.Database.SqlServer.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_field_in_the_secrets_list_is_blanked_carried_and_restored()
    {
        var settings = WithSecrets();
        foreach (var secret in SettingsSecrets.In(settings))
            secret.Set($"value-of-{secret.Key}");

        var path = TempFile();
        try
        {
            SettingsPortability.Export(settings, path, "correct horse");
            var text = File.ReadAllText(path);
            foreach (var secret in SettingsSecrets.In(settings))
                Assert.DoesNotContain(secret.Get(), text);

            var imported = SettingsPortability.Import(path, "correct horse", out _, out _);
            Assert.Equal(SettingsSecrets.KnownValues(settings), SettingsSecrets.KnownValues(imported!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_wrong_passphrase_imports_nothing_and_says_which_it_was()
    {
        var path = TempFile();
        try
        {
            SettingsPortability.Export(WithSecrets(), path, "right");

            Assert.Null(SettingsPortability.Import(path, "wrong", out var problem, out var needs));
            Assert.True(needs);
            Assert.Contains("passphrase", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The one-argument import is what every older caller uses, and a protected file must not slip past
    /// it as a settings file with every credential blank.
    /// </summary>
    [Fact]
    public void A_protected_file_read_without_a_passphrase_is_refused_rather_than_emptied()
    {
        var path = TempFile();
        try
        {
            SettingsPortability.Export(WithSecrets(), path, "right");
            Assert.Null(SettingsPortability.Import(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_a_passphrase_the_file_is_the_one_this_application_always_wrote()
    {
        var path = TempFile();
        try
        {
            SettingsPortability.Export(WithSecrets(), path);

            Assert.False(SettingsPortability.IsProtected(path));
            var imported = SettingsPortability.Import(path, out var problem);
            Assert.NotNull(imported);
            Assert.Equal("", problem);
            Assert.Equal("", imported!.AiProviderInstances[0].ApiKey);
            Assert.Equal("", imported.Database.SqlServer.Password);
            Assert.DoesNotContain("SecretVault", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_secret_that_will_not_decrypt_arrives_empty_rather_than_as_rubbish()
    {
        var (protection, key) = PassphraseVault.Create("right");

        Assert.Equal("secret", PassphraseVault.Decrypt(PassphraseVault.Encrypt("secret", key), key));

        var other = PassphraseVault.Open(protection, "right", out _)!;
        Assert.Null(PassphraseVault.Decrypt("not base64 at all", other));
        Assert.Equal("", PassphraseVault.Encrypt("", key));
    }

    /// <summary>The iteration count is read out of the file, and the file is somebody else's: a count
    /// this version will not spend is refused with a sentence rather than derived for hours on the
    /// thread that asked.</summary>
    [Fact]
    public void An_iteration_count_above_the_ceiling_is_refused_rather_than_run()
    {
        var (protection, _) = PassphraseVault.Create("right");
        protection.Iterations = PassphraseVault.MaxIterations + 1;

        Assert.Null(PassphraseVault.Open(protection, "right", out var problem));
        Assert.Contains("rounds", problem);
    }

    /// <summary>
    /// The other half of the Kind rule: a connections file carries no property the settings know, so it
    /// reads here as the defaults — importing it would replace the whole dialog with them. Refused by
    /// name, the mirror of what the connections importer does to a settings export.
    /// </summary>
    [Fact]
    public void A_connections_file_is_refused_rather_than_read_as_default_settings()
    {
        var path = TempFile();
        try
        {
            ManualConnectionsPortability.Export(
                [new ManualDatabaseConnection { Id = "c1", Server = "db1", Database = "orders" }],
                path, "");

            Assert.Null(SettingsPortability.Import(path, out var problem));
            Assert.Contains("connections file", problem);
            Assert.Null(SettingsPortability.Import(path, "any passphrase", out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
