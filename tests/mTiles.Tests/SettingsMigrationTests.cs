using System.Diagnostics;
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

        Assert.False(service.Settings.GitIgnoreWorkspaceDir);
    }

    [Fact]
    public void An_explicit_yes_under_the_old_name_stays_yes()
    {
        GivenSettings("""{ "GitHideMTerminalDir": true }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
    }

    /// <summary>
    /// The custom shell cannot be honoured — a shell is a class now — so the least the migration owes
    /// its user is a line in the log and a settings file that stops carrying a key nothing reads.
    /// </summary>
    [Fact]
    public void A_custom_shell_is_reported_and_dropped()
    {
        GivenSettings("""{ "CustomShellPath": "/opt/nu/bin/nu", "CustomShellArgs": "--login" }""");

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            var service = new SettingsService(SettingsPath);

            Assert.Null(service.Settings.LegacyCustomShellPath);
            Assert.Null(service.Settings.LegacyCustomShellArgs);
            Assert.Contains("/opt/nu/bin/nu", listener.Text);
            Assert.Contains("--login", listener.Text);
        }
        finally { Trace.Listeners.Remove(listener); }

        // Read once: the save the migration triggers is what takes the keys out of the file, so the
        // next version's reader is not still being handed a setting nothing can act on.
        Assert.DoesNotContain("CustomShellPath", File.ReadAllText(SettingsPath));
    }

    /// <summary>
    /// The same loss by the other route: on Unix the old detection offered whatever <c>$SHELL</c>
    /// pointed at, so the default could be a shell no class here knows. Falling back to bash without a
    /// word is the silent version of the custom-shell loss.
    /// </summary>
    [Fact]
    public void A_default_shell_this_version_does_not_know_is_reported()
    {
        GivenSettings("""{ "DefaultShellName": "nu" }""");

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            var service = new SettingsService(SettingsPath);

            Assert.Contains("nu", listener.Text);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    /// <summary>
    /// The name itself is kept, because "this build does not know it" is also what a shell added by a
    /// newer version looks like after a Velopack rollback. Dropping it would let the older build settle
    /// the question for the newer one, permanently.
    /// </summary>
    [Fact]
    public void A_default_shell_this_version_does_not_know_is_kept()
    {
        GivenSettings("""{ "DefaultShellName": "nu" }""");

        _ = new SettingsService(SettingsPath);

        Assert.Equal("nu", new SettingsService(SettingsPath).Settings.DefaultShellName);
        Assert.Contains("\"DefaultShellName\": \"nu\"", File.ReadAllText(SettingsPath));
    }

    /// <summary>Said once, not on every launch — which is what makes keeping the value affordable.</summary>
    [Fact]
    public void An_unknown_default_shell_is_not_reported_twice()
    {
        GivenSettings("""{ "DefaultShellName": "nu" }""");
        _ = new SettingsService(SettingsPath);

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            _ = new SettingsService(SettingsPath);
            Assert.DoesNotContain("default shell setting", listener.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    /// <summary>A name that becomes known again forgets it was reported, so the same loss is said out
    /// loud once more if the shell ever disappears from the catalog again.</summary>
    [Fact]
    public void A_shell_that_becomes_known_again_clears_the_report()
    {
        GivenSettings("""{ "DefaultShellName": "nu" }""");
        _ = new SettingsService(SettingsPath);

        var service = new SettingsService(SettingsPath);
        service.Settings.DefaultShellName = "bash";
        service.Save();

        _ = new SettingsService(SettingsPath);

        Assert.Equal("", new SettingsService(SettingsPath).Settings.ReportedUnknownShellName);
    }

    /// <summary>A shell the catalog does know survives untouched — including under the display name a
    /// build without ids wrote, which is the whole reason <c>Find</c> matches both.</summary>
    [Theory]
    [InlineData("bash")]
    [InlineData("PowerShell")]
    public void A_known_default_shell_is_left_alone(string named)
    {
        GivenSettings($$"""{ "DefaultShellName": "{{named}}" }""");

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            Assert.Equal(named, new SettingsService(SettingsPath).Settings.DefaultShellName);
            Assert.DoesNotContain("default shell setting", listener.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    /// <summary>A file that never named one is left alone — and, in particular, not saved: every
    /// migration here is a read of something that is usually absent.</summary>
    [Fact]
    public void Settings_without_a_custom_shell_say_nothing()
    {
        GivenSettings("""{ "FontSize": 13 }""");

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            _ = new SettingsService(SettingsPath);
            Assert.DoesNotContain("custom shell", listener.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private sealed class CapturedTrace : TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();
        public string Text => _text.ToString();
        public override void Write(string? message) => _text.Append(message);
        public override void WriteLine(string? message) => _text.AppendLine(message);
    }

    /// <summary>Never having said anything is not the same as having said no: those users get the
    /// default, which is what a new installation gets too.</summary>
    [Fact]
    public void Settings_that_never_mentioned_it_take_the_default()
    {
        GivenSettings("""{ "FontSize": 13 }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
    }

    /// <summary>
    /// With more than one of these keys present the <b>newest</b> answer wins, and each is dropped
    /// once it has been read.
    /// </summary>
    /// <remarks>
    /// <para>There are three generations of this setting now — <c>GitHideMTerminalDir</c>,
    /// <c>GitIgnoreMTerminalDir</c> and <c>GitIgnoreWorkspaceDir</c> — and with three, "the oldest
    /// wins" stops being caution and becomes an answer nobody can change: somebody who said no years
    /// ago and yes last week would be held to the no for as long as the key survived. Applying them in
    /// order and letting the later override is the only rule that reads a file as a history rather
    /// than a vote.</para>
    /// <para>This is a change: the two-generation version deliberately let the older key win, on the
    /// grounds that it marked a pre-rename file and the cautious reading should carry. That argument
    /// does not survive a third name, and a file holding two of them is reachable only by hand anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public void With_several_keys_present_the_newest_answer_wins_and_they_are_then_gone()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false, "GitIgnoreMTerminalDir": true }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);

        var written = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("GitHideMTerminalDir", written);
        Assert.DoesNotContain("GitIgnoreMTerminalDir", written);
    }

    /// <summary>The name between the two renames. It is the one most existing installations actually
    /// hold, so it is the hop that matters in practice rather than in principle.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_answer_under_the_middle_name_is_carried_across(bool answered)
    {
        GivenSettings($$"""{ "GitIgnoreMTerminalDir": {{(answered ? "true" : "false")}} }""");

        Assert.Equal(answered, new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
        Assert.DoesNotContain("GitIgnoreMTerminalDir", File.ReadAllText(SettingsPath));
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
        Assert.False(new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
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
    /// Nothing seeds, edits or removes a shell profile any more — the list is only read.
    /// </summary>
    /// <remarks>
    /// It is still what <c>AgentTileMigration</c> matches a saved tile's <c>userProfileId</c> against, so
    /// touching it here would take somebody's agent tiles with it a launch before the workspace holding
    /// them is even opened. Three migrations used to run over this list; the assertion that replaced
    /// them is that a settings file comes back with exactly the profiles it went in with — including a
    /// broken one, which is now nobody's to fix.
    /// </remarks>
    [Fact]
    public void The_shell_profiles_in_a_settings_file_are_read_and_never_touched()
    {
        GivenSettings("""
            {
              "ShellProfiles": [
                { "Name": "OpenCode", "StartupScript": "opencode --session ${tileId}", "FallbackScript": "opencode" }
              ]
            }
            """);

        var profile = Assert.Single(new SettingsService(SettingsPath).Settings.ShellProfiles);

        Assert.Equal("OpenCode", profile.Name);
        Assert.Equal("opencode --session ${tileId}", profile.StartupScript);
        Assert.Equal("opencode", profile.FallbackScript);
    }

    /// <summary>And a fresh installation gets none at all.</summary>
    /// <remarks>An AI CLI in a shell is an agent tile now, so seeding four profiles would be offering a
    /// route that no longer leads anywhere — and one the empty tile's chooser could not show.</remarks>
    [Fact]
    public void A_new_installation_is_seeded_with_no_profiles_and_one_instance_per_agent()
    {
        var settings = new SettingsService(SettingsPath).Settings;

        Assert.Empty(settings.ShellProfiles);
        Assert.Equal(
            mTiles.Services.Agents.AiAgentCatalog.All.Select(a => a.Id).Order(),
            settings.AiAgentInstances.Select(i => i.AgentId).Order());
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
    /// <para>Two cases, not one per property: which properties refuse a null is <c>SettingsNullGuardTests</c>'
    /// business, and it walks the whole graph by reflection rather than listing them. What is left for
    /// this file is that the guards are reached through a real file on disk — once at the top level, and
    /// once <em>one level deeper</em>, which is where patching the sections after loading stopped
    /// working and where a null is a window that never appears.</para>
    /// </remarks>
    [Theory]
    [InlineData("""{ "Speech": null, "Database": null, "ShellProfiles": null, "AiAgentInstances": null, "AiProviderInstances": null }""")]
    [InlineData("""{ "Speech": { "CustomWords": null }, "Database": { "ManualConnections": null, "SqlServer": null, "PostgreSql": null } }""")]
    public void A_null_section_takes_its_defaults_rather_than_breaking_startup(string json)
    {
        GivenSettings(json);

        var settings = new SettingsService(SettingsPath).Settings;

        Assert.NotNull(settings.Speech);
        Assert.NotNull(settings.Database);
        Assert.NotNull(settings.ShellProfiles);
        Assert.NotNull(settings.AiAgentInstances);
        Assert.NotNull(settings.AiProviderInstances);
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
    /// <para><c>ColorThemeName</c> is looked up by name during theme setup, <c>GitPath</c> is handed to
    /// <c>Path</c> and to a process start, and <c>DefaultShellName</c> is looked up in the shell
    /// catalog.</para>
    /// </remarks>
    [Fact]
    public void A_null_string_anywhere_in_the_settings_arrives_empty()
    {
        GivenSettings("""
            {
              "ColorThemeName": null,
              "GitPath": null,
              "DefaultShellName": null,
              "Database": { "PostgreSql": { "Username": null, "Password": null } }
            }
            """);

        var settings = new SettingsService(SettingsPath).Settings;

        Assert.Equal("", settings.ColorThemeName);
        Assert.Equal("", settings.GitPath);
        Assert.Equal("", settings.DefaultShellName);
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
    /// <c>NotSupportedException</c> on the first save. <c>AiAgentInstance.ExtraEnv</c> is a string-keyed
    /// dictionary on the startup path, so getting that wrong would stop the settings saving at all: a
    /// worse failure than the one the converter is for, and one that only shows at run time.
    /// <para>Its <em>values</em> are nullable, and that is load-bearing rather than incidental: a null
    /// there means "unset this variable", so a converter that turned it into an empty string would set
    /// the variable to nothing instead of removing it.</para>
    /// </remarks>
    [Fact]
    public void A_dictionary_of_strings_still_survives_a_save_and_a_load()
    {
        var service = new SettingsService(SettingsPath);
        var instance = service.Settings.AiAgentInstances[0];
        instance.ExtraEnv["ANTHROPIC_BASE_URL"] = "https://example.invalid";
        instance.ExtraEnv["ANTHROPIC_API_KEY"] = null;
        service.Save();

        var reloaded = new SettingsService(SettingsPath).Settings.AiAgentInstances
            .First(i => i.Id == instance.Id);

        Assert.Equal("https://example.invalid", reloaded.ExtraEnv["ANTHROPIC_BASE_URL"]);
        Assert.Null(reloaded.ExtraEnv["ANTHROPIC_API_KEY"]);
    }

}