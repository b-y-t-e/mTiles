using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What happens to settings written by an older version.
/// <para>Renaming a property means the old value is not read and the new one starts at its default —
/// harmless for a font size, and not harmless when the default writes to the user's repository.</para>
/// </summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* a temp directory */ }
    }

    private void GivenSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>
    /// The one case where the application would edit a repository against a decision the user had
    /// already made. They turned the old switch off; the feature was renamed and its meaning widened
    /// from hiding to ignoring, but "no, thank you" carries across both readings.
    /// </summary>
    [Fact]
    public void An_explicit_no_under_the_old_name_is_still_a_no()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false }""");

        var service = new SettingsService(SettingsPath);

        Assert.False(service.Settings.GitIgnoreMTerminalDir);
    }

    [Fact]
    public void An_explicit_yes_under_the_old_name_stays_yes()
    {
        GivenSettings("""{ "GitHideMTerminalDir": true }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }

    /// <summary>Never having said anything is not the same as having said no: those users get the
    /// default, which is what a new installation gets too.</summary>
    [Fact]
    public void Settings_that_never_mentioned_it_take_the_default()
    {
        GivenSettings("""{ "FontSize": 13 }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }

    /// <summary>
    /// With both keys present the old one wins, and that is the intended rule rather than an accident.
    /// <para>The old key exists only in a file written by a version that did not know the new one, so
    /// its presence is what marks the file as coming from before the rename — and it is removed as soon
    /// as it has been read, so it can never override a choice made afterwards. A file holding both is
    /// only reachable by hand-editing, and there the more cautious reading wins: the one that can leave
    /// somebody's repository alone.</para>
    /// </summary>
    [Fact]
    public void With_both_keys_present_the_old_answer_wins_and_is_then_gone()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false, "GitIgnoreMTerminalDir": true }""");

        Assert.False(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
        Assert.DoesNotContain("GitHideMTerminalDir", File.ReadAllText(SettingsPath));
    }

    /// <summary>Read once. The old key is gone from the file afterwards, so the migration cannot run a
    /// second time and undo a choice the user makes later.</summary>
    [Fact]
    public void The_old_key_is_dropped_once_it_has_been_read()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false }""");
        _ = new SettingsService(SettingsPath);

        Assert.DoesNotContain("GitHideMTerminalDir", File.ReadAllText(SettingsPath));

        // And a run after that leaves the answer where the first one put it.
        Assert.False(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }

    /// <summary>
    /// The dictation shortcut lost its separate on/off switch, and an empty shortcut is what "off"
    /// means now. Somebody who had switched it off must not get Alt+Space swallowed again by an update.
    /// </summary>
    [Fact]
    public void A_shortcut_that_was_switched_off_comes_back_as_no_shortcut()
    {
        GivenSettings("""{ "Speech": { "Hotkey": "Alt+Space", "HotkeyEnabled": false } }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal("", service.Settings.Speech.Hotkey);
        Assert.DoesNotContain("HotkeyEnabled", File.ReadAllText(SettingsPath));
    }

    /// <summary>Switched on means exactly what it means now: there is a shortcut, and it is that one.</summary>
    [Fact]
    public void A_shortcut_that_was_switched_on_keeps_working()
    {
        GivenSettings("""{ "Speech": { "Hotkey": "Ctrl+Alt+D", "HotkeyEnabled": true } }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal("Ctrl+Alt+D", service.Settings.Speech.Hotkey);
        Assert.DoesNotContain("HotkeyEnabled", File.ReadAllText(SettingsPath));
    }

    /// <summary>
    /// The seeded OpenCode profile learns to resume its session, which seeding alone cannot teach it.
    /// </summary>
    /// <remarks>
    /// Profiles are only ever added and never overwritten, so everybody who has run this app before
    /// already has an "OpenCode" profile and would never see the new one. The pair being replaced cannot
    /// work at all: <c>opencode --session ${tileId}</c> hands over an id without opencode's required
    /// <c>ses</c> prefix, so it was always refused and the tile always fell through to a bare
    /// <c>opencode</c> with no history.
    /// </remarks>
    [Fact]
    public void The_old_opencode_profile_is_replaced_by_one_that_can_resume()
    {
        GivenSettings("""
            {
              "ShellProfiles": [
                { "Name": "OpenCode", "StartupScript": "opencode --session ${tileId}", "FallbackScript": "opencode" }
              ]
            }
            """);

        var profile = Assert.Single(new SettingsService(SettingsPath).Settings.ShellProfiles,
            p => p.Name == "OpenCode");

        Assert.Equal("opencode --session ses_${tileId}", profile.StartupScript);
        Assert.Contains("opencode import", profile.FallbackScript);
        Assert.Contains("${opencodeSessionFile}", profile.FallbackScript);
    }

    /// <summary>
    /// A profile the user has touched is left exactly as they wrote it.
    /// </summary>
    /// <remarks>
    /// The migration replaces a command that cannot work, not a decision. Somebody who edited either
    /// script — even to something that also does not work — has said what they want their tile to run,
    /// and an update is not the moment to overrule them.
    /// </remarks>
    [Theory]
    [InlineData("opencode --continue", "opencode")]
    [InlineData("opencode --session ${tileId}", "opencode --print-logs")]
    public void An_opencode_profile_the_user_edited_is_left_alone(string startup, string fallback)
    {
        GivenSettings($$"""
            {
              "ShellProfiles": [
                { "Name": "OpenCode", "StartupScript": "{{startup}}", "FallbackScript": "{{fallback}}" }
              ]
            }
            """);

        var profile = Assert.Single(new SettingsService(SettingsPath).Settings.ShellProfiles,
            p => p.Name == "OpenCode");

        Assert.Equal(startup, profile.StartupScript);
        Assert.Equal(fallback, profile.FallbackScript);
    }

    /// <summary>A fresh installation gets the resuming profile from seeding, so the migration has
    /// nothing to do and the two cannot both fire.</summary>
    [Fact]
    public void A_new_installation_is_seeded_with_the_resuming_opencode_profile()
    {
        var profile = Assert.Single(new SettingsService(SettingsPath).Settings.ShellProfiles,
            p => p.Name == "OpenCode");

        Assert.Equal("opencode --session ses_${tileId}", profile.StartupScript);
        Assert.Contains("${opencodeSessionFile}", profile.FallbackScript);
    }

    /// <summary>
    /// A fresh installation starts in the language the machine is set up in — but only a fresh one.
    /// </summary>
    /// <remarks>
    /// It is a first guess, not a preference to keep re-applying: somebody who chose <c>auto</c> on
    /// purpose would otherwise find it overwritten on every start. Only the whisper models are told the
    /// language at all; Parakeet works it out for itself.
    /// </remarks>
    [Fact]
    public void A_new_installation_starts_in_the_systems_language()
    {
        var service = new SettingsService(SettingsPath);          // no file yet
        var language = service.Settings.Speech.Language;

        // Whatever this machine is set to, the answer is one of the languages Settings offers — never a
        // code that only exists in the file. The rule itself is the theory below; this is the wiring:
        // that a fresh installation actually runs it.
        Assert.Contains(language,
            mTiles.Services.Speech.SpeechModelCatalog.Languages.Select(l => l.Code));
    }

    /// <summary>
    /// A settings file that cannot be read is a first run, and says so.
    /// </summary>
    /// <remarks>
    /// The distinction that matters is not "is there a file" but "did anything come out of it". A file
    /// truncated by a full disk, hand-edited into invalid JSON, or written by something else leaves this
    /// object holding defaults — a fresh installation in every respect except that the first-run steps
    /// were skipped, because <c>File.Exists</c> had said otherwise. The user then starts with dictation
    /// set to <c>auto</c> and nothing anywhere explaining why.
    /// </remarks>
    [Theory]
    [InlineData("{ not json at all ")]
    [InlineData("")]
    [InlineData("null")]
    public void A_settings_file_that_cannot_be_read_is_treated_as_a_first_run(string content)
    {
        // Constructed against nothing, then handed a file that says nothing usable. Asked after
        // construction on purpose: the constructor *repairs* what it could not read — the first-run
        // steps save — so by then there is a perfectly good file there, and asking would measure the
        // repair rather than the rule.
        var service = new SettingsService(SettingsPath);
        GivenSettings(content);

        Assert.False(service.Load());                                   // nothing came out of it
        Assert.Equal(30, service.Settings.Speech.ModelUnloadMinutes);   // and the defaults are in place
    }

    /// <summary>
    /// A settings file that could not be read is kept before anything overwrites it.
    /// </summary>
    /// <remarks>
    /// Treating it as a first run is only half the story: the first-run steps <em>save</em>, so within
    /// milliseconds the file is replaced by defaults. It holds every profile the user wrote, their AI
    /// tool paths, their manual database connections and the passwords for them — and "unreadable" is
    /// very often a truncation with most of the content still sitting there, or a file this version
    /// happens not to understand. Overwriting it silently, to repair a fault nobody has been told about,
    /// is the worst of the available outcomes.
    /// </remarks>
    [Fact]
    public void An_unreadable_settings_file_is_kept_before_it_is_overwritten()
    {
        GivenSettings("""{ "FontSize": 13, "GitPath": "C:\\keep\\me" """);   // truncated, as a full disk leaves it

        _ = new SettingsService(SettingsPath);

        var kept = Directory.GetFiles(_directory, "settings.bad-*.json");
        var copy = Assert.Single(kept);
        Assert.Contains("C:\\\\keep\\\\me", File.ReadAllText(copy));

        // And the repair really did happen on top of it, so the copy is the only place that text is left.
        Assert.DoesNotContain("keep", File.ReadAllText(SettingsPath));
    }

    /// <summary>A file that was read is not copied: this is not a backup feature, it is a rescue.</summary>
    [Fact]
    public void A_readable_settings_file_is_not_copied()
    {
        GivenSettings("""{ "FontSize": 13 }""");

        _ = new SettingsService(SettingsPath);

        Assert.Empty(Directory.GetFiles(_directory, "settings.bad-*.json"));
    }

    [Fact]
    public void A_settings_file_that_can_be_read_is_not_a_first_run()
    {
        GivenSettings("""{ "Speech": { "ModelUnloadMinutes": 7 } }""");

        var service = new SettingsService(SettingsPath);

        Assert.True(service.Load());
        Assert.Equal(7, service.Settings.Speech.ModelUnloadMinutes);
    }

    /// <summary>
    /// The rule, as a function of the machine's language rather than of the machine.
    /// </summary>
    /// <remarks>
    /// It used to be tested by recomputing it — <c>CurrentUICulture</c> and the same lookup — and
    /// asserting the result against itself, on whatever culture the build agent happened to have. That
    /// passes on a broken implementation as readily as on a working one, and says nothing at all on an
    /// agent set to a language nobody here speaks.
    /// </remarks>
    [Theory]
    [InlineData("pl", "pl")]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("zh", "auto")]     // a real language, not one this app offers
    [InlineData("", "auto")]
    [InlineData("xx", "auto")]
    public void The_starting_language_is_the_system_one_only_when_it_is_offered(string system, string expected)
        => Assert.Equal(expected, SettingsService.StartingLanguage(system));

    [Fact]
    public void An_existing_installation_keeps_the_language_it_had()
    {
        GivenSettings("""{ "Speech": { "Language": "auto" } }""");

        Assert.Equal("auto", new SettingsService(SettingsPath).Settings.Speech.Language);
    }

    /// <summary>
    /// A section the file says is <c>null</c> takes its defaults instead of stopping the application.
    /// </summary>
    /// <remarks>
    /// A property initialiser is no guarantee: deserialising <c>"Speech": null</c> overwrites the fresh
    /// object with nothing and is not an error, so the load's own catch never sees it. The first service
    /// to read it then throws during construction of the main window — the application does not start,
    /// and says nothing about why. Settings are never worth refusing to launch over.
    /// </remarks>
    [Theory]
    [InlineData("""{ "Speech": null }""")]
    [InlineData("""{ "Database": null }""")]
    [InlineData("""{ "ShellProfiles": null, "CustomAiTools": null, "CustomAiToolPaths": null }""")]
    [InlineData("""{ "GoalDefaultModels": null }""")]
    // One level deeper, which is where patching the sections after loading stopped working: each of
    // these is dereferenced during startup, so a null is a window that never appears.
    [InlineData("""{ "Speech": { "CustomWords": null } }""")]
    [InlineData("""{ "Database": { "ManualConnections": null } }""")]
    [InlineData("""{ "Database": { "SqlServer": null, "PostgreSql": null } }""")]
    public void A_null_section_takes_its_defaults_rather_than_breaking_startup(string json)
    {
        GivenSettings(json);

        var settings = new SettingsService(SettingsPath).Settings;

        Assert.NotNull(settings.Speech);
        Assert.NotNull(settings.Database);
        Assert.NotNull(settings.ShellProfiles);
        Assert.NotNull(settings.CustomAiTools);
        Assert.NotNull(settings.CustomAiToolPaths);
        Assert.NotNull(settings.GoalDefaultModels);
        Assert.NotNull(settings.Speech.CustomWords);
        Assert.NotNull(settings.Database.ManualConnections);
        Assert.NotNull(settings.Database.SqlServer);
        Assert.NotNull(settings.Database.PostgreSql);

        // And the defaults are the real ones, not just non-null.
        Assert.Equal(new mTiles.Models.SpeechSettings().ModelUnloadMinutes, settings.Speech.ModelUnloadMinutes);
    }

    /// <summary>
    /// A <c>null</c> string anywhere in the settings arrives as an empty one.
    /// </summary>
    /// <remarks>
    /// <para>The rule used to be written property by property, and only on the properties somebody had
    /// already been bitten by — the four in <c>SpeechSettings</c>. The settings tree has dozens of strings
    /// across seven types, and a list of the ones already found is not a defence against the next one.
    /// These four are chosen for being on the startup path and *not* individually guarded: none of them
    /// had a setter that refused a null before the converter existed.</para>
    /// <para><c>ColorThemeName</c> is looked up by name during theme setup, <c>GitPath</c> and the shell
    /// paths are handed to <c>Path</c> and to a process start.</para>
    /// </remarks>
    [Fact]
    public void A_null_string_anywhere_in_the_settings_arrives_empty()
    {
        GivenSettings("""
            {
              "ColorThemeName": null,
              "GitPath": null,
              "CustomShellPath": null,
              "Database": { "PostgreSql": { "Username": null, "Password": null } }
            }
            """);

        var settings = new SettingsService(SettingsPath).Settings;

        Assert.Equal("", settings.ColorThemeName);
        Assert.Equal("", settings.GitPath);
        Assert.Equal("", settings.CustomShellPath);
        Assert.Equal("", settings.Database.PostgreSql.Username);
        // The encrypted ones carry their own converter, which wins over the general rule — so they are
        // the last strings that could still have come back null, and they need saying separately.
        Assert.Equal("", settings.Database.PostgreSql.Password);
    }

    /// <summary>
    /// The dictionaries still round-trip.
    /// </summary>
    /// <remarks>
    /// A custom converter for <c>string</c> is used for dictionary <em>keys</em> as well, and one that
    /// does not implement the property-name pair does not quietly fall back — it throws
    /// <c>NotSupportedException</c> on the first save. <c>CustomAiToolPaths</c> and
    /// <c>GoalDefaultModels</c> are both <c>Dictionary&lt;string, string&gt;</c>, so getting that wrong
    /// would have stopped the settings saving at all: a worse failure than the one the converter is for,
    /// and one that only shows up at run time.
    /// </remarks>
    [Fact]
    public void A_dictionary_of_strings_still_survives_a_save_and_a_load()
    {
        var service = new SettingsService(SettingsPath);
        service.Settings.CustomAiToolPaths["claude"] = @"C:\tools\claude.exe";
        service.Settings.GoalDefaultModels["claude"] = "opus";
        service.Save();

        var reloaded = new SettingsService(SettingsPath).Settings;

        Assert.Equal(@"C:\tools\claude.exe", reloaded.CustomAiToolPaths["claude"]);
        Assert.Equal("opus", reloaded.GoalDefaultModels["claude"]);
    }
}
