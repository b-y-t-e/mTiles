using System.Diagnostics;
using mTiles.Models;
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
    private readonly TempDirectory _directory = new();

    private string SettingsPath => Path.Combine(_directory.Path, "settings.json");

    public void Dispose() => _directory.Dispose();

    private void GivenSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>
    /// An answer under either older name of the <c>.gitignore</c> switch is carried across and the old key
    /// is dropped once read, so the migration cannot run again and undo a choice made later.
    /// </summary>
    /// <remarks>An explicit no matters most: it is the one case where the application would otherwise
    /// edit a repository against a decision the user had already made. The middle name is the one most
    /// installations hold.</remarks>
    [Theory]
    [InlineData("GitHideMTerminalDir", false)]
    [InlineData("GitHideMTerminalDir", true)]
    [InlineData("GitIgnoreMTerminalDir", false)]
    [InlineData("GitIgnoreMTerminalDir", true)]
    public void An_answer_under_an_old_name_is_carried_across_and_the_key_dropped(string key, bool answered)
    {
        GivenSettings($$"""{ "{{key}}": {{(answered ? "true" : "false")}} }""");

        Assert.Equal(answered, new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
        Assert.DoesNotContain(key, File.ReadAllText(SettingsPath));
        Assert.Equal(answered, new SettingsService(SettingsPath).Settings.GitIgnoreWorkspaceDir);
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
    /// A default shell this version does not know — on Unix the old detection offered whatever
    /// <c>$SHELL</c> pointed at — is reported rather than silently replaced, and the name itself is kept:
    /// it is also what a shell added by a newer version looks like after a Velopack rollback.
    /// </summary>
    [Fact]
    public void A_default_shell_this_version_does_not_know_is_reported_and_kept()
    {
        GivenSettings("""{ "DefaultShellName": "nu" }""");

        var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        try
        {
            _ = new SettingsService(SettingsPath);
            Assert.Contains("nu", listener.Text);
        }
        finally { Trace.Listeners.Remove(listener); }

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

    /// <summary>
    /// The Goal tile's one effort level became a preset over four roles, and the whole chain that
    /// carries a stored level across is exercised here rather than only its pure rule.
    /// </summary>
    /// <remarks>
    /// <c>GoalRolesTests</c> argues <c>GoalRoles.FromLegacyEffort</c> on its own, which leaves the
    /// parts between the file and it untested: the <c>GoalEffort</c> property name, the tolerant
    /// converter on a nullable enum, and the rule that the key is dropped once read. Any of the three
    /// silently reduces the migration to a no-op — <c>LegacyGoalEffort</c> stays null, somebody's
    /// <c>max</c> comes back as <c>balanced</c>, and nothing anywhere fails.
    /// </remarks>
    [Theory]
    [InlineData("xhigh", GoalEffortPreset.Thorough)]
    [InlineData("max", GoalEffortPreset.Thorough)]
    [InlineData("low", GoalEffortPreset.Cheap)]
    [InlineData("ToolDefault", GoalEffortPreset.ToolDefault)]
    // The old default was not a decision, so it arrives as the new default rather than as high everywhere.
    [InlineData("high", GoalEffortPreset.Balanced)]
    public void A_stored_effort_level_becomes_the_preset_it_meant(string stored, GoalEffortPreset expected)
    {
        GivenSettings($$"""{ "GoalEffort": "{{stored}}" }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal(expected, service.Settings.GoalEffortPreset);
        Assert.Null(service.Settings.LegacyGoalEffort);

        // Read once: the save the migration triggers is what takes the key out of the file, so a later
        // version is not still handed a level nothing acts on.
        Assert.DoesNotContain("GoalEffort\"", File.ReadAllText(SettingsPath));
        Assert.Equal(expected, new SettingsService(SettingsPath).Settings.GoalEffortPreset);
    }

    /// <summary>
    /// The word moved under the preset, so a file that named one is moved with it.
    /// </summary>
    /// <remarks>
    /// <c>balanced</c> named plan medium · work low · review <em>high</em> until the scale gained a
    /// rung beneath it, and the value is stored by name — so left alone, somebody who had chosen it
    /// would come back with their reviews quietly shallower, which is the one thing
    /// <c>docs/GOAL.md</c>'s measurement says costs findings. Every other word means today what it
    /// meant then and is passed through, or `cheap` would arrive as a decision nobody made.
    /// </remarks>
    [Theory]
    [InlineData("Balanced", GoalEffortPreset.Careful)]
    [InlineData("Thorough", GoalEffortPreset.Thorough)]
    [InlineData("Cheap", GoalEffortPreset.Cheap)]
    [InlineData("ToolDefault", GoalEffortPreset.ToolDefault)]
    public void A_preset_stored_under_the_old_vocabulary_keeps_its_levels(
        string stored, GoalEffortPreset expected)
    {
        GivenSettings($$"""{ "GoalEffortPreset": "{{stored}}" }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal(expected, service.Settings.GoalEffortPreset);
        Assert.Null(service.Settings.LegacyGoalEffortPreset);

        // Read once, and the migration's own save is what drops the old key — otherwise it would run
        // again on every launch and overwrite whatever the user had chosen since.
        Assert.Equal(expected, new SettingsService(SettingsPath).Settings.GoalEffortPreset);
    }

    /// <summary>A preset chosen since the rename is left exactly where it is.</summary>
    /// <remarks>The half the test above cannot cover: both keys are readable at once, and a migration
    /// that ran on the new one too would move every user onto <c>careful</c> on the first launch after
    /// they had chosen <c>balanced</c>.</remarks>
    [Fact]
    public void A_preset_stored_under_the_current_key_is_not_migrated()
    {
        GivenSettings("""{ "GoalEffortPresetV2": "Balanced" }""");

        Assert.Equal(GoalEffortPreset.Balanced, new SettingsService(SettingsPath).Settings.GoalEffortPreset);
    }

    /// <summary>
    /// A level this build cannot read must not cost the file, and must not be read as a decision
    /// either: the converter answers null, so the preset stays at its default.
    /// </summary>
    [Fact]
    public void An_unreadable_effort_level_leaves_the_default_standing()
    {
        GivenSettings("""{ "GoalEffort": "cosmic", "FontSize": 13 }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal(GoalEffortPreset.Balanced, service.Settings.GoalEffortPreset);
        Assert.Equal(13, service.Settings.FontSize);
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

    /// <summary>
    /// The dictation shortcut lost its separate on/off switch, and an empty shortcut is what "off" means
    /// now: somebody who had switched it off must not get Alt+Space swallowed again by an update.
    /// </summary>
    [Theory]
    [InlineData("Alt+Space", false, "")]
    [InlineData("Ctrl+Alt+D", true, "Ctrl+Alt+D")]
    public void The_old_shortcut_switch_becomes_the_shortcut_or_none(string hotkey, bool enabled, string expected)
    {
        GivenSettings($$"""{ "Speech": { "Hotkey": "{{hotkey}}", "HotkeyEnabled": {{(enabled ? "true" : "false")}} } }""");

        var service = new SettingsService(SettingsPath);

        Assert.Equal(expected, service.Settings.Speech.Hotkey);
        Assert.DoesNotContain("HotkeyEnabled", File.ReadAllText(SettingsPath));
    }

    /// <summary>
    /// Nothing seeds, edits or removes a shell profile any more — the list is only read.
    /// </summary>
    /// <remarks>
    /// It is still what <c>TerminalAgentTileMigration</c> matches a saved tile's <c>userProfileId</c> against, so
    /// touching it here would take somebody's terminal agent tiles with it a launch before the workspace holding
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
    /// <remarks>An AI CLI in a shell is a terminal agent tile now, so seeding four profiles would be offering a
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

        var kept = Directory.GetFiles(_directory.Path, "settings.bad-*.json");
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

        Assert.Empty(Directory.GetFiles(_directory.Path, "settings.bad-*.json"));
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
    /// <para>Which properties refuse a null is <c>SettingsNullGuardTests</c>' business; this is that the
    /// guards are reached through a real file on disk — at the top level and one level deeper, which is
    /// where patching the sections after loading stopped working.</para>
    /// </remarks>
    [Fact]
    public void A_null_section_takes_its_defaults_rather_than_breaking_startup()
    {
        GivenSettings("""{ "Speech": { "CustomWords": null }, "Database": null, "ShellProfiles": null, "AiAgentInstances": null, "AiProviderInstances": null }""");

        var settings = new SettingsService(SettingsPath).Settings;

        Assert.NotNull(settings.Speech);
        Assert.NotNull(settings.Database);
        Assert.NotNull(settings.ShellProfiles);
        Assert.NotNull(settings.AiAgentInstances);
        Assert.NotNull(settings.AiProviderInstances);
        Assert.NotNull(settings.Speech.CustomWords);
        Assert.NotNull(settings.Database.ManualConnections);

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
              "LastWorkspaceId": null,
              "ShellProfiles": [ { "Name": "mine", "RequiredAiToolBinaryName": null } ],
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
        // And it overrules a string?: the converter is chosen by type and never told which property it
        // fills, so the annotation holds only for what code assigns (see SettingsNullGuardTests).
        Assert.Equal("", settings.LastWorkspaceId);
        Assert.Equal("", settings.ShellProfiles.Single(p => p.Name == "mine").RequiredAiToolBinaryName);
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

    /// <summary>A font family nobody chose moves to the copy that now ships with the application.</summary>
    /// <remarks>The old defaults named fonts that may or may not be installed. A stored value equal to
    /// one of them is what a settings file gets for having been written at all, so leaving it would
    /// mean the embedded typeface reached nobody who had ever run this application.</remarks>
    [Fact]
    public void The_old_default_font_is_replaced_by_the_embedded_one()
    {
        GivenSettings($$"""
            {
              "FontFamily": "{{AppDefaults.PreviousFontFamilies[0]}}",
              "TerminalFontFamily": "{{AppDefaults.PreviousTerminalFontFamilies[0]}}"
            }
            """);

        var service = new SettingsService(SettingsPath);

        Assert.Equal(AppDefaults.FontFamily, service.Settings.FontFamily);
        Assert.Equal(AppDefaults.TerminalFontFamily, service.Settings.TerminalFontFamily);
        // Written back, or it would be migrated again on every launch and never survive an edit.
        Assert.Contains("JetBrainsMono", File.ReadAllText(SettingsPath));
    }

    /// <summary>A font the user typed is theirs, and is left exactly as it is.</summary>
    [Fact]
    public void A_chosen_font_is_not_replaced()
    {
        GivenSettings("""
            { "FontFamily": "Comic Sans MS", "TerminalFontFamily": "Fira Code, monospace" }
            """);

        var service = new SettingsService(SettingsPath);

        Assert.Equal("Comic Sans MS", service.Settings.FontFamily);
        Assert.Equal("Fira Code, monospace", service.Settings.TerminalFontFamily);
    }
}
