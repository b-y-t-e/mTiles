using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Settings out of this machine and back into another one, and what does not travel with them.
/// </summary>
public class SettingsPortabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-portability").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
        GC.SuppressFinalize(this);
    }

    private static AppSettings WithSecrets() => new()
    {
        FontSize = 17,
        AiProviderInstances =
        [
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "Work", ApiKey = "sk-secret" },
        ],
        Database = { SqlServer = { Password = "db-secret" } },
    };

    /// <summary>
    /// The export carries the configuration and none of the keys.
    /// </summary>
    /// <remarks>Both halves are asserted, and the first is why: an export that stripped the whole
    /// provider rather than its key would leave nothing to import.</remarks>
    [Fact]
    public void Export_keeps_the_configuration_and_drops_the_secrets()
    {
        var path = Path.Combine(_dir, "out.json");
        SettingsPortability.Export(WithSecrets(), path);

        var read = SettingsPortability.Import(path, out var problem);

        Assert.Equal("", problem);
        Assert.NotNull(read);
        Assert.Equal(17, read.FontSize);
        Assert.Equal("Work", Assert.Single(read.AiProviderInstances).Name);
        Assert.Equal("", read.AiProviderInstances[0].ApiKey);
        Assert.Equal("", read.Database.SqlServer.Password);
    }

    /// <summary>
    /// The exported file is owner-only, because what crosses in it can still be a secret.
    /// </summary>
    /// <remarks><c>ExtraEnv</c> is exported as the user wrote it, so a token can be in there; on Unix
    /// the umask would otherwise make the file world-readable.</remarks>
    [Fact]
    public void The_exported_file_is_owner_only()
    {
        var path = Path.Combine(_dir, "out.json");
        SettingsPortability.Export(WithSecrets(), path);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }

    /// <summary>Exporting reads the settings; it must not empty the ones the application is running on.
    /// </summary>
    [Fact]
    public void Export_does_not_disturb_the_settings_it_was_given()
    {
        var settings = WithSecrets();
        SettingsPortability.Export(settings, Path.Combine(_dir, "out.json"));

        Assert.Equal("sk-secret", settings.AiProviderInstances[0].ApiKey);
        Assert.Equal("db-secret", settings.Database.SqlServer.Password);
    }

    /// <summary>
    /// An import keeps the keys already configured here, matched by the instance's own id.
    /// </summary>
    /// <remarks>Without this the one thing that cannot be typed back from memory is the one thing an
    /// import destroys — a file meant to add configuration emptying the working key of every provider.
    /// </remarks>
    [Fact]
    public void Import_keeps_the_keys_this_machine_already_has()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", ApiKey = "sk-secret" });
        service.Settings.Database.SqlServer.Password = "db-secret";

        var path = Path.Combine(_dir, "out.json");
        SettingsPortability.Export(WithSecrets(), path);
        service.Replace(SettingsPortability.Import(path, out _)!);

        Assert.Equal("sk-secret", service.Settings.AiProviderInstances[0].ApiKey);
        Assert.Equal("db-secret", service.Settings.Database.SqlServer.Password);
        Assert.Equal(17, service.Settings.FontSize);
    }

    /// <summary>
    /// An import keeps the password of a manual database connection this machine already has.
    /// </summary>
    /// <remarks>The export blanks it, so a file exported from this machine and imported back into it
    /// would otherwise empty every connection the user cannot retype from memory — the same failure the
    /// id-matched restore was written to prevent, one field further down.</remarks>
    [Fact]
    public void Import_keeps_the_manual_connection_passwords_this_machine_already_has()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        var mine = new ManualDatabaseConnection { Id = "c1", Alias = "Lab", Password = "conn-secret" };
        service.Settings.Database.ManualConnections.Add(mine);

        var exported = new AppSettings();
        exported.Database.ManualConnections.Add(
            new ManualDatabaseConnection { Id = "c1", Alias = "Lab", Password = "conn-secret" });
        var path = Path.Combine(_dir, "out.json");
        SettingsPortability.Export(exported, path);

        service.Replace(SettingsPortability.Import(path, out _)!);

        Assert.Equal("conn-secret", Assert.Single(service.Settings.Database.ManualConnections).Password);
    }

    /// <summary>
    /// An imported file that predates an agent still gets that agent its instance.
    /// </summary>
    /// <remarks>Seeding runs in the constructor, so without it in <c>Replace</c> the agent would be
    /// missing from the tile chooser and from the Goal tile's list until the next restart, with nothing
    /// on screen saying why.</remarks>
    [Fact]
    public void Import_seeds_the_agents_the_file_does_not_know_about()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        var seeded = service.Settings.AiAgentInstances.Count;
        Assert.True(seeded > 0);

        var path = Path.Combine(_dir, "out.json");
        SettingsPortability.Export(new AppSettings(), path);
        service.Replace(SettingsPortability.Import(path, out _)!);

        Assert.Equal(seeded, service.Settings.AiAgentInstances.Count);
    }

    /// <summary>An imported file written under an older name for a setting is still read.</summary>
    /// <remarks>Same reason as the seeding: the constructor is not the only way a settings object gets
    /// here, and the user's own "no" to writing in their repository must survive an import too.</remarks>
    [Fact]
    public void Import_runs_the_legacy_migration()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));

        var older = new AppSettings { LegacyGitIgnoreMTerminalDir = false };
        service.Replace(older);

        Assert.False(service.Settings.GitIgnoreWorkspaceDir);
    }

    /// <summary>
    /// After an import the dialog shows the imported values on every page, not just the three the
    /// import obviously touches.
    /// </summary>
    /// <remarks>Speech and the default shell are the ones that mattered: those pages save as you type,
    /// so a stale field is not merely stale — the first control the user touches writes it back over
    /// what was just imported, with nothing on screen to say so.</remarks>
    [Fact]
    public async Task Import_refreshes_the_pages_that_save_as_you_type()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        service.Settings.Speech.Hotkey = "Alt+Space";
        service.Settings.Speech.AutoSubmitEnter = false;
        service.Settings.Phone.Port = 1;

        var incoming = new AppSettings();
        incoming.Speech.Hotkey = "Ctrl+Alt+D";
        incoming.Speech.AutoSubmitEnter = true;
        incoming.Phone.Port = 4321;
        var path = Path.Combine(_dir, "in.json");
        SettingsPortability.Export(incoming, path);

        var vm = new SettingsViewModel(service)
        {
            BrowseOpenFile = () => Task.FromResult<string?>(path),
            ConfirmAction = _ => Task.FromResult(true),
        };

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Ctrl+Alt+D", vm.SpeechHotkey);
        Assert.True(vm.SpeechAutoSubmit);
        Assert.Equal(4321, vm.PhonePort);

        // And the page did not save its old copy back on the way through.
        Assert.Equal("Ctrl+Alt+D", service.Settings.Speech.Hotkey);
        Assert.Equal(4321, service.Settings.Phone.Port);
    }

    /// <summary>A file that is not settings answers with a reason rather than throwing at the picker.
    /// </summary>
    [Fact]
    public void An_unreadable_file_is_reported_rather_than_thrown()
    {
        var path = Path.Combine(_dir, "not-settings.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(SettingsPortability.Import(path, out var problem));
        Assert.NotEqual("", problem);
    }
}
