using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Whether the database form has anything worth saving — the one thing that decides if the pinned
/// Save bar is on screen.
/// <para>It matters more here than elsewhere in Settings: everything else persists as you type, while
/// this restarts the database service and so has to be applied deliberately. An edit that is never
/// applied looks exactly like one that was, which is what the bar exists to stop.</para>
/// </summary>
public sealed class DatabaseSettingsDirtyTests : IDisposable
{
    private readonly TempSettings _settings = new();

    public void Dispose() => _settings.Dispose();

    /// <summary>A view model in the state the dialog is in when the user is looking at the form: the
    /// database fields are loaded on entering the tab, not in the constructor, and a test that skipped
    /// that would be asserting about a form nobody has filled in yet.</summary>
    private SettingsViewModel OnTheDatabaseTab()
    {
        var vm = new SettingsViewModel(_settings.Service);
        vm.SelectTabCommand.Execute(DatabaseTab);
        return vm;
    }

    // The view model's own constant, not a copy of its value: the numbers are what SettingsTabs
    // exists to stop anybody writing out, and a test that hardcodes 3 goes on passing after the
    // tabs are reordered while every assertion in it has quietly moved to the wrong page.
    private const int DatabaseTab = SettingsTabs.Database;

    [Fact]
    public void A_form_that_matches_the_stored_settings_has_nothing_to_save()
        => Assert.False(OnTheDatabaseTab().HasUnsavedDatabaseChanges);

    /// <summary>
    /// A view model whose Database tab nobody has opened holds a form of type defaults — an empty
    /// username, an empty password, no ports. Those differ from anything stored, but that is not the
    /// user having changed something, and saving them writes the blanks over real credentials.
    /// </summary>
    [Fact]
    public void A_form_nobody_has_opened_has_nothing_to_save()
    {
        var db = _settings.Service.Settings.Database;
        db.SqlServer.Username = "sa";
        db.SqlServer.Password = "secret";
        db.PostgreSql.Ports = [5432, 5433];

        var vm = new SettingsViewModel(_settings.Service);   // the tab is never opened

        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>And if something reaches the command anyway, it refuses rather than writing blanks over
    /// the settings. Nothing in the UI can reach it today — which is exactly the kind of guarantee that
    /// stops being true one refactor later, and the cost here is the user's credentials.</summary>
    [Fact]
    public void Saving_a_form_nobody_has_opened_does_not_wipe_the_settings()
    {
        var db = _settings.Service.Settings.Database;
        db.SqlServer.Username = "sa";
        db.SqlServer.Password = "secret";
        db.PostgreSql.Ports = [5432, 5433];

        var vm = new SettingsViewModel(_settings.Service);
        vm.SaveDatabaseSettingsCommand.Execute(null);

        Assert.Equal("sa", db.SqlServer.Username);
        Assert.Equal("secret", db.SqlServer.Password);
        Assert.Equal([5432, 5433], db.PostgreSql.Ports);
    }

    /// <summary>
    /// Driven from the set the view model actually watches, not a list written out by hand — that list
    /// had silently missed both password fields, which are the ones where a lost edit costs the most.
    /// <see cref="Change"/> throws for a name it does not know, so a field added to the set without a
    /// case here fails loudly instead of going unwatched.
    /// </summary>
    [Fact]
    public void Editing_any_field_of_the_form_raises_the_flag()
    {
        Assert.NotEmpty(SettingsViewModel.DatabaseFormFieldNames);

        foreach (var field in SettingsViewModel.DatabaseFormFieldNames)
        {
            var vm = OnTheDatabaseTab();
            Assert.False(vm.HasUnsavedDatabaseChanges, $"{field}: the form did not start clean");

            Change(vm, field);

            Assert.True(vm.HasUnsavedDatabaseChanges, $"editing {field} left the form looking saved");
        }
    }

    /// <summary>Applying brings it down again — the form and the store are equal by construction at
    /// that point, and nothing else raises a notification to say so.</summary>
    [Fact]
    public void Applying_the_changes_puts_the_bar_away()
    {
        var vm = OnTheDatabaseTab();
        vm.DbHttpPort = 18091;
        Assert.True(vm.HasUnsavedDatabaseChanges);

        vm.SaveDatabaseSettingsCommand.Execute(null);

        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>Editing back to the stored value is not a change. Without comparing against the store —
    /// a plain "something was typed" flag — the bar would stay up for the rest of the session.</summary>
    [Fact]
    public void Typing_a_value_and_undoing_it_leaves_nothing_to_save()
    {
        var vm = OnTheDatabaseTab();
        int original = vm.DbHttpPort;

        vm.DbHttpPort = original + 1;
        Assert.True(vm.HasUnsavedDatabaseChanges);

        vm.DbHttpPort = original;
        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>
    /// The port list is text in the form and numbers in the settings, and saving normalises it. Compared
    /// through that same normalisation, so a list the user spelled untidily does not read as an unsaved
    /// change for ever — the form would never spell it the way saving does.
    /// </summary>
    [Fact]
    public void A_port_list_that_only_differs_in_spelling_is_not_a_change()
    {
        var vm = OnTheDatabaseTab();
        vm.DbPostgreSqlPorts = "5432, 5433";
        vm.SaveDatabaseSettingsCommand.Execute(null);

        vm.DbPostgreSqlPorts = " 5432 ,5433, ";

        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>
    /// The bar has to survive the user reacting to it. Leaving the tab and coming back reloaded the
    /// form from the store, which threw the edits away and put the bar down — so the feature stopped
    /// working at exactly the moment someone used it.
    /// </summary>
    [Fact]
    public void Leaving_the_tab_and_coming_back_keeps_the_unsaved_changes()
    {
        var vm = OnTheDatabaseTab();
        vm.DbSqlServerUsername = "someone";

        vm.SelectTabCommand.Execute(0);         // General
        vm.SelectTabCommand.Execute(DatabaseTab);

        Assert.Equal("someone", vm.DbSqlServerUsername);
        Assert.True(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>With nothing pending, coming back does reload — otherwise a change made elsewhere would
    /// never show up in the form.</summary>
    [Fact]
    public void Coming_back_with_nothing_pending_reloads_the_form()
    {
        var vm = OnTheDatabaseTab();
        _settings.Service.Settings.Database.SqlServer.Username = "changed elsewhere";

        vm.SelectTabCommand.Execute(0);
        vm.SelectTabCommand.Execute(DatabaseTab);

        Assert.Equal("changed elsewhere", vm.DbSqlServerUsername);
        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>
    /// A value the save would throw away is still a value the field is showing, and nobody will ever
    /// store it. Comparing after clamping made that read as "saved": the form said 80, the settings
    /// said 1024, and there was nothing on screen to say the two disagreed.
    /// </summary>
    [Fact]
    public void A_port_the_save_would_clamp_away_still_counts_as_unsaved()
    {
        var vm = OnTheDatabaseTab();
        vm.DbHttpPort = 1024;
        vm.SaveDatabaseSettingsCommand.Execute(null);

        vm.DbHttpPort = 80;                     // below the floor: saving would turn it back into 1024

        Assert.True(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>The same for a port the parser drops. Untidy spacing is not the same thing — that is
    /// the user writing the same list differently, not naming something that cannot be used.</summary>
    [Theory]
    [InlineData("5432, 99999", true)]
    [InlineData("5432, abc", true)]
    [InlineData(" 5432 ,, ", false)]
    public void A_port_the_save_would_discard_counts_as_unsaved(string ports, bool unsaved)
    {
        var vm = OnTheDatabaseTab();
        vm.DbPostgreSqlPorts = "5432";
        vm.SaveDatabaseSettingsCommand.Execute(null);

        vm.DbPostgreSqlPorts = ports;

        Assert.Equal(unsaved, vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>Saving normalises the interval, and the field is brought along — without that, 0 stays
    /// on screen against a stored 30 and the form reads as unsaved for the rest of the session.</summary>
    [Fact]
    public void Saving_a_blank_interval_settles_rather_than_staying_unsaved()
    {
        var vm = OnTheDatabaseTab();
        vm.DbDiscoveryInterval = 0;

        vm.SaveDatabaseSettingsCommand.Execute(null);

        Assert.Equal(30, vm.DbDiscoveryInterval);
        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>
    /// Saving used to force the bar away whatever happened. That was true for the fields saving
    /// normalises and false for anything it rejects: a port the parser drops stayed in the box while the
    /// settings never received it, and the bar vanished as though it had been applied.
    /// </summary>
    [Fact]
    public void Saving_a_port_list_the_parser_rejects_does_not_pretend_it_was_applied()
    {
        var vm = OnTheDatabaseTab();
        vm.DbPostgreSqlPorts = "5432, 99999";

        vm.SaveDatabaseSettingsCommand.Execute(null);

        // The field now shows what was actually stored, and with the two agreeing the bar can go.
        Assert.Equal("5432", vm.DbPostgreSqlPorts);
        Assert.Equal([5432], _settings.Service.Settings.Database.PostgreSql.Ports);
        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    // ---- closing the dialog ----------------------------------------------------

    [Fact]
    public async Task Closing_with_nothing_pending_asks_nothing()
    {
        var vm = OnTheDatabaseTab();
        var asked = false;
        vm.ConfirmAction = _ => { asked = true; return Task.FromResult(true); };

        Assert.True(await vm.TryCloseAsync());
        Assert.False(asked);
    }

    /// <summary>Closing looks like finishing. A change that restarts the database service should not be
    /// left pending by a gesture that means "I'm done here" without a word about it.</summary>
    [Fact]
    public async Task Closing_with_unapplied_changes_asks_first()
    {
        var vm = OnTheDatabaseTab();
        vm.DbSqlServerUsername = "someone";
        string? question = null;
        vm.ConfirmAction = message => { question = message; return Task.FromResult(false); };

        Assert.False(await vm.TryCloseAsync());
        Assert.NotNull(question);
        Assert.Equal(DatabaseTab, vm.SelectedTab);      // and shows which changes it means
        Assert.Equal("someone", vm.DbSqlServerUsername);
        Assert.True(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>Saying yes discards. Closing while leaving the edits in place would bring the bar back
    /// the next time Settings opened, which is not what "discard" asked for.</summary>
    [Fact]
    public async Task Discarding_puts_the_form_back_and_lets_the_dialog_close()
    {
        var vm = OnTheDatabaseTab();
        var original = vm.DbSqlServerUsername;
        vm.DbSqlServerUsername = "someone";
        vm.ConfirmAction = _ => Task.FromResult(true);

        Assert.True(await vm.TryCloseAsync());
        Assert.Equal(original, vm.DbSqlServerUsername);
        Assert.False(vm.HasUnsavedDatabaseChanges);
    }

    /// <summary>With nothing wired up to ask, the dialog still closes. Not being able to put the
    /// question is not a reason to trap someone in a window they cannot leave.</summary>
    [Fact]
    public async Task Closing_with_no_way_to_ask_still_closes()
    {
        var vm = OnTheDatabaseTab();
        vm.DbSqlServerUsername = "someone";
        vm.ConfirmAction = null;

        Assert.True(await vm.TryCloseAsync());
    }

    // ---- closing the application -----------------------------------------------

    /// <summary>
    /// Closing the window is the commonest way of saying "I'm done", and without asking here the
    /// protection on the dialog has a hole exactly there: the edits survive right up until the view
    /// model holding them is thrown away.
    /// </summary>
    [Fact]
    public async Task Shutting_down_with_unapplied_changes_asks_and_can_be_called_off()
    {
        var main = NewMainWindow();
        main.Settings.SelectTabCommand.Execute(DatabaseTab);
        main.Settings.DbSqlServerUsername = "someone";
        main.Settings.ConfirmAction = _ => Task.FromResult(false);

        Assert.False(await main.ConfirmShutdownAsync());
        Assert.True(main.IsSettingsOpen);       // "go back" has to lead somewhere visible
        Assert.Equal("someone", main.Settings.DbSqlServerUsername);
    }

    /// <summary>Saying yes discards and lets the application go.</summary>
    [Fact]
    public async Task Shutting_down_and_discarding_lets_the_application_close()
    {
        var main = NewMainWindow();
        main.Settings.SelectTabCommand.Execute(DatabaseTab);
        var original = main.Settings.DbSqlServerUsername;
        main.Settings.DbSqlServerUsername = "someone";
        main.Settings.ConfirmAction = _ => Task.FromResult(true);

        Assert.True(await main.ConfirmShutdownAsync());
        Assert.Equal(original, main.Settings.DbSqlServerUsername);
    }

    [Fact]
    public async Task Shutting_down_with_nothing_pending_asks_nothing()
    {
        var main = NewMainWindow();
        var asked = false;
        main.Settings.ConfirmAction = _ => { asked = true; return Task.FromResult(true); };

        Assert.True(await main.ConfirmShutdownAsync());
        Assert.False(asked);
    }

    /// <summary>The whole main view model, so this covers the wiring and not just the piece it calls —
    /// which is the part that was missing.</summary>
    private MainWindowViewModel NewMainWindow() =>
        new(_settings.Workspaces, _settings.Layouts, _settings.Service,
            TestTiles.Catalog(_settings.Service));

    private static void Change(SettingsViewModel vm, string field)
    {
        switch (field)
        {
            case "DbEnabled": vm.DbEnabled = !vm.DbEnabled; break;
            case "DbHttpPort": vm.DbHttpPort = vm.DbHttpPort + 1; break;
            case "DbSqlServerEnabled": vm.DbSqlServerEnabled = !vm.DbSqlServerEnabled; break;
            case "DbSqlServerIntegrated": vm.DbSqlServerIntegrated = !vm.DbSqlServerIntegrated; break;
            case "DbSqlServerUsername": vm.DbSqlServerUsername += "x"; break;
            case "DbPostgreSqlEnabled": vm.DbPostgreSqlEnabled = !vm.DbPostgreSqlEnabled; break;
            case "DbPostgreSqlUsername": vm.DbPostgreSqlUsername += "x"; break;
            case "DbSqlServerPassword": vm.DbSqlServerPassword += "x"; break;
            case "DbPostgreSqlPassword": vm.DbPostgreSqlPassword += "x"; break;
            case "DbPostgreSqlPorts": vm.DbPostgreSqlPorts = "5555"; break;
            case "DbDiscoveryInterval": vm.DbDiscoveryInterval = vm.DbDiscoveryInterval + 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "no such field");
        }
    }
}
