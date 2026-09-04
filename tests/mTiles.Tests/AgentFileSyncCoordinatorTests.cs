using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The coordinator against a real temporary workspace: what it writes down is only ever an answer the
/// user actually gave, and what it starts and stops is a live engine.
/// </summary>
[Collection(AgentFileSyncTests.CollectionName)]
public sealed class AgentFileSyncCoordinatorTests : IAsyncLifetime
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-agentsync-coord-" + Guid.NewGuid().ToString("N"));
    private AgentFileSyncCoordinator? _coordinator;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _coordinator?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
        return Task.CompletedTask;
    }

    private string Claude => Path.Combine(_dir, AgentFileSyncEngine.ClaudeFileName);
    private string Agents => Path.Combine(_dir, AgentFileSyncEngine.AgentsFileName);
    private string ConfigFile => Path.Combine(_dir, ".mtiles", "agent-file-sync.json");

    private SettingsService? _settings;

    private AgentFileSyncCoordinator NewCoordinator()
    {
        _settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        _coordinator = new AgentFileSyncCoordinator(_settings);
        return _coordinator;
    }

    private static IEnumerable<IAiAgent> NoAgents() => [];

    /// <summary>How long to wait before asserting that nothing happened. Comfortably past the engine's
    /// debounce, and short because a negative is settled by the first quiet moment rather than needing
    /// the timeout a positive does.</summary>
    private const int QuietMs = 600;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Condition was not met within the timeout.");
    }

    /// <summary>The global switch suppresses the question rather than answering it, so nothing is
    /// written down while it is off. Turning it back on is therefore the moment a loaded workspace
    /// becomes askable, and nothing else would bring the question back until its tile tree changed or
    /// it was re-opened.</summary>
    [Fact]
    public async Task Turning_the_global_switch_back_on_asks_a_workspace_that_was_never_asked()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        _settings!.Settings.AgentFileSyncEnabled = false;

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());
        Assert.False(File.Exists(ConfigFile));

        var asked = new TaskCompletionSource();
        coordinator.ShowWizard = _ =>
        {
            asked.TrySetResult();
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };
        await Task.Delay(QuietMs);
        Assert.False(asked.Task.IsCompleted);

        _settings.Settings.AgentFileSyncEnabled = true;
        _settings.NotifyChanged();

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => coordinator.IsEnabled(_dir));
    }

    [Fact]
    public async Task No_wizard_wired_records_no_answer()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");

        await NewCoordinator().EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.False(File.Exists(ConfigFile));
        Assert.False(_coordinator!.IsEnabled(_dir));
    }

    /// <summary>The window is built after the last workspace has been restored, so the first-open
    /// question always comes up before there is anywhere to ask it. It is held, not dropped.</summary>
    [Fact]
    public async Task A_question_asked_too_early_is_replayed_once_the_wizard_is_wired()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());
        Assert.False(File.Exists(ConfigFile));

        var asked = new TaskCompletionSource();
        coordinator.ShowWizard = _ =>
        {
            asked.TrySetResult();
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => coordinator.IsEnabled(_dir));
    }

    /// <summary>A workspace the user has closed is one nobody should be shown a dialog about.</summary>
    [Fact]
    public async Task A_held_question_is_dropped_when_the_workspace_is_unloaded()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        coordinator.Unload(_dir);

        var asked = 0;
        coordinator.ShowWizard = _ =>
        {
            asked++;
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };

        await Task.Delay(QuietMs);
        Assert.Equal(0, asked);
        Assert.False(File.Exists(ConfigFile));
    }

    [Fact]
    public async Task Declining_is_remembered_and_not_asked_again()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var asked = 0;
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ =>
        {
            asked++;
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(false, null));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());
        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.Equal(1, asked);
        Assert.True(File.Exists(ConfigFile));
        Assert.False(coordinator.IsEnabled(_dir));
    }

    [Fact]
    public async Task Enabling_overwrites_the_file_the_user_did_not_pick()
    {
        File.WriteAllText(Claude, "from claude");
        File.WriteAllText(Agents, "from agents");
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = request =>
        {
            Assert.Equal(AgentFileSyncWizardMode.AskEnableAndPickAuthoritative, request.Mode);
            return Task.FromResult<AgentFileSyncWizardResult?>(
                new AgentFileSyncWizardResult(true, AgentFileSyncEngine.ClaudeFileName));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.True(coordinator.IsEnabled(_dir));
        Assert.Equal("from claude", File.ReadAllText(Agents));
    }

    /// <summary>Turning sync on by hand still needs an answer when the two files disagree: picking a
    /// winner from the mtimes would overwrite one of them in the user's repository without asking.
    /// </summary>
    [Fact]
    public async Task Explicit_enable_without_a_wizard_changes_nothing()
    {
        File.WriteAllText(Claude, "older");
        File.WriteAllText(Agents, "newer");
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddMinutes(-5));

        var coordinator = NewCoordinator();
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        Assert.False(coordinator.IsEnabled(_dir));
        Assert.False(File.Exists(ConfigFile));
        Assert.Equal("older", File.ReadAllText(Claude));
        Assert.Equal("newer", File.ReadAllText(Agents));
    }

    /// <summary>Two files that already agree are no conflict, so there is nothing to ask about and the
    /// toggle works with no wizard wired at all.</summary>
    [Fact]
    public async Task Explicit_enable_without_a_conflict_needs_no_wizard()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");

        var coordinator = NewCoordinator();
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        Assert.True(coordinator.IsEnabled(_dir));

        File.WriteAllText(Claude, "two");
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two");
    }

    [Fact]
    public async Task Unloading_stops_the_engine_and_keeps_the_answer()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        File.WriteAllText(Claude, "two");
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two");

        coordinator.Unload(_dir);
        File.WriteAllText(Claude, "three");
        await Task.Delay(QuietMs);

        Assert.Equal("two", File.ReadAllText(Agents));
        Assert.True(coordinator.IsEnabled(_dir));
    }

    /// <summary>The same interleaving one workspace down: unloading while the dialog is open must not
    /// be undone by the answer that arrives afterwards.</summary>
    [Fact]
    public async Task Unloading_while_the_wizard_is_open_leaves_nothing_running()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ =>
        {
            coordinator.Unload(_dir);
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        File.WriteAllText(Claude, "two");
        await Task.Delay(QuietMs);

        Assert.Equal("one", File.ReadAllText(Agents));
    }

    /// <summary>Shutting down while the dialog is open is the one interleaving the coordinator cannot
    /// order for itself: the answer arrives after <see cref="AgentFileSyncCoordinator.Dispose"/> has
    /// already stopped every engine it could see, so whatever this call would otherwise start has to be
    /// taken back down by the call itself.</summary>
    [Fact]
    public async Task Disposing_while_the_wizard_is_open_leaves_nothing_running()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ =>
        {
            coordinator.Dispose();
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        File.WriteAllText(Claude, "two");
        await Task.Delay(QuietMs);

        Assert.Equal("one", File.ReadAllText(Agents));
    }

    /// <summary>The panel's toggle starts an engine for a row that was never opened, and the removal
    /// path that unloads a workspace view model cannot reach it. UnloadAll is what does.</summary>
    [Fact]
    public async Task Unloading_everything_stops_an_engine_no_workspace_view_model_owns()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        File.WriteAllText(Claude, "two");
        await WaitUntilAsync(() => File.ReadAllText(Agents) == "two");

        coordinator.UnloadAll();
        File.WriteAllText(Claude, "three");
        await Task.Delay(QuietMs);

        Assert.Equal("two", File.ReadAllText(Agents));
    }

    /// <summary>A directory that has gone since the layout was saved makes the watcher's constructor
    /// throw. It must not take the coordinator down with it, and it must not leave anything behind.
    /// </summary>
    [Fact]
    public async Task A_workspace_directory_that_is_gone_starts_nothing_and_throws_nothing()
    {
        File.WriteAllText(Claude, "one");
        File.WriteAllText(Agents, "one");
        var coordinator = NewCoordinator();
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);
        coordinator.UnloadAll();

        var gone = Path.Combine(_dir, "not-there");
        await coordinator.SetWorkspaceEnabledAsync(gone, enabled: true);
    }

    /// <summary>The toggle reaches a workspace nobody has opened, so nothing has cleared up after the
    /// old one-line <c>@AGENTS.md</c> import. Left there it reads as a differing CLAUDE.md, and the one
    /// true answer — "my CLAUDE.md is the current one" — would replace the whole of AGENTS.md with that
    /// line.</summary>
    [Fact]
    public async Task Enabling_a_never_opened_workspace_takes_the_legacy_shim_out_first()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        File.WriteAllText(Agents, "the real instructions");
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ => throw new InvalidOperationException(
            "There is no conflict left to ask about once the shim has gone.");

        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        Assert.Equal("the real instructions", File.ReadAllText(Agents));
        Assert.Equal("the real instructions", File.ReadAllText(Claude));
    }

    /// <summary>The toggle can be reached with the global switch off — the menu item's visibility is
    /// decided when the menu is built — and the engine it asks for will not run. Taking the legacy shim
    /// out ahead of a start that never comes would leave the workspace with no file Claude Code reads
    /// at all, and nothing here recreates it.</summary>
    [Fact]
    public async Task Enabling_with_the_global_switch_off_leaves_the_shim_where_it_is()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        File.WriteAllText(Agents, "the real instructions");
        var coordinator = NewCoordinator();
        _settings!.Settings.AgentFileSyncEnabled = false;

        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        Assert.Equal("@AGENTS.md" + Environment.NewLine, File.ReadAllText(Claude));
        Assert.Equal("the real instructions", File.ReadAllText(Agents));

        // And the mirror really is off, not silently half-on: the recorded answer waits for the switch.
        File.WriteAllText(Claude, "edited while the switch is off");
        await Task.Delay(QuietMs);
        Assert.Equal("the real instructions", File.ReadAllText(Agents));
    }

    /// <summary>The answer the toggle recorded while the global switch was off is what applies the
    /// moment the switch comes back on — and the engine that starts then finds the shim still on disk.
    /// It holds none of the user's words, so it is not one of two versions to settle by mtime: the
    /// seeding is told to trust AGENTS.md.</summary>
    [Fact]
    public async Task Turning_the_global_switch_on_later_takes_the_surviving_shim_out()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        File.WriteAllText(Agents, "the real instructions");
        var coordinator = NewCoordinator();
        _settings!.Settings.AgentFileSyncEnabled = false;
        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);
        Assert.Equal("@AGENTS.md" + Environment.NewLine, File.ReadAllText(Claude));

        _settings.Settings.AgentFileSyncEnabled = true;
        _settings.NotifyChanged();

        await WaitUntilAsync(() => File.ReadAllText(Claude) == "the real instructions");
        Assert.Equal("the real instructions", File.ReadAllText(Agents));
    }

    /// <summary>Two workspaces asking at once is the ordinary case — every held question is replayed
    /// together the moment the window wires the dialog — and two modals over one owner window overlap.
    /// </summary>
    [Fact]
    public async Task Only_one_wizard_is_ever_open_at_a_time()
    {
        var second = Path.Combine(_dir, "second");
        Directory.CreateDirectory(second);
        foreach (var dir in new[] { _dir, second })
        {
            File.WriteAllText(Path.Combine(dir, AgentFileSyncEngine.ClaudeFileName), "one");
            File.WriteAllText(Path.Combine(dir, AgentFileSyncEngine.AgentsFileName), "one");
        }

        var coordinator = NewCoordinator();
        var open = 0;
        var overlapped = false;
        coordinator.ShowWizard = async _ =>
        {
            if (Interlocked.Increment(ref open) > 1) overlapped = true;
            await Task.Delay(100);
            Interlocked.Decrement(ref open);
            return new AgentFileSyncWizardResult(false, null);
        };

        await Task.WhenAll(
            coordinator.EvaluateWorkspaceAsync(_dir, NoAgents()),
            coordinator.EvaluateWorkspaceAsync(second, NoAgents()));

        Assert.False(overlapped, "Two wizards were open at the same time.");
    }

    /// <summary>The mirror only ever moves content from one file to the other, so what it writes has to
    /// be the bytes it read: decoded through the default UTF-8, a UTF-16 file comes back mangled and a
    /// BOM is dropped from the copy.</summary>
    [Fact]
    public async Task Seeding_the_mirror_copies_the_bytes_rather_than_the_decoded_text()
    {
        var source = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes("zażółć gęślą jaźń")).ToArray();
        File.WriteAllBytes(Claude, source);
        var coordinator = NewCoordinator();

        await coordinator.SetWorkspaceEnabledAsync(_dir, enabled: true);

        Assert.Equal(source, File.ReadAllBytes(Agents));
    }

    /// <summary>The shim is the only file Claude Code reads. A user who declines the wizard, or who has
    /// the feature switched off globally, must be left exactly as they were — deleting it would take
    /// their project instructions away from the one agent that cannot find AGENTS.md.</summary>
    [Fact]
    public async Task Declining_leaves_the_legacy_shim_where_it_is()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        File.WriteAllText(Agents, "the real instructions");
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ => Task.FromResult<AgentFileSyncWizardResult?>(
            new AgentFileSyncWizardResult(false, null));

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.Equal("@AGENTS.md" + Environment.NewLine, File.ReadAllText(Claude));
        Assert.Equal("the real instructions", File.ReadAllText(Agents));
    }

    /// <summary>A shim holds none of the user's own words, so there is nothing to pick between: the
    /// wizard asks the plain yes/no, and saying yes takes the shim out and mirrors AGENTS.md over it.
    /// </summary>
    [Fact]
    public async Task The_legacy_shim_is_never_offered_as_a_version_to_choose()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        File.WriteAllText(Agents, "the real instructions");
        var coordinator = NewCoordinator();
        AgentFileSyncWizardMode? asked = null;
        coordinator.ShowWizard = request =>
        {
            asked = request.Mode;
            return Task.FromResult<AgentFileSyncWizardResult?>(new AgentFileSyncWizardResult(true, null));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.Equal(AgentFileSyncWizardMode.AskEnableOnly, asked);
        Assert.Equal("the real instructions", File.ReadAllText(Agents));
        Assert.Equal("the real instructions", File.ReadAllText(Claude));
    }

    /// <summary>The old import is recognised even where the AGENTS.md it names has been deleted since:
    /// enabling the sync takes the shim out and seeds nothing, rather than creating a new AGENTS.md
    /// whose whole content is the circular <c>@AGENTS.md</c> — which codex, pi and agy read as the
    /// project's instructions.</summary>
    [Fact]
    public async Task Enabling_sync_where_the_shims_target_is_gone_creates_no_circular_agents_file()
    {
        File.WriteAllText(Claude, "@AGENTS.md" + Environment.NewLine);
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ => Task.FromResult<AgentFileSyncWizardResult?>(
            new AgentFileSyncWizardResult(true, null));

        await coordinator.EvaluateWorkspaceAsync(_dir, [AiAgentCatalog.Find("codex")!]);

        Assert.True(coordinator.IsEnabled(_dir));
        Assert.False(File.Exists(Agents));

        // The mirror is live over an empty pair — the first thing written either way is carried
        // across, which is what "sync enabled" means here.
        File.WriteAllText(Agents, "the real instructions");
        await WaitUntilAsync(() =>
            File.Exists(Claude) && File.ReadAllText(Claude) == "the real instructions");
    }

    /// <summary>The wizard's answer is which file is current, not only whether to sync. An answer whose
    /// engine start was cut off by an unload must still name that file when the workspace is opened
    /// again: settling the still-disagreeing pair by mtime instead would overwrite exactly the file the
    /// user named — the loss the epoch machinery prevents within one run.</summary>
    [Fact]
    public async Task The_wizards_choice_outlives_an_unload_during_the_dialog()
    {
        File.WriteAllText(Claude, "from claude");
        File.WriteAllText(Agents, "from agents");
        // The mtimes point the other way: the newest file is the one the user did not pick.
        File.SetLastWriteTimeUtc(Claude, DateTime.UtcNow.AddMinutes(-5));
        var coordinator = NewCoordinator();
        coordinator.ShowWizard = _ =>
        {
            coordinator.Unload(_dir);
            return Task.FromResult<AgentFileSyncWizardResult?>(
                new AgentFileSyncWizardResult(true, AgentFileSyncEngine.ClaudeFileName));
        };

        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        // The answer was recorded even though the start it asked for was cut off...
        Assert.True(coordinator.IsEnabled(_dir));
        var persisted = AgentFileSyncConfigStore.Load(_dir);
        Assert.Equal(AgentFileSyncEngine.ClaudeFileName, persisted.AuthoritativeFileName);
        Assert.Equal("from agents", File.ReadAllText(Agents));

        // ...and opening the workspace again starts from what was recorded, not from the mtimes.
        await coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());

        Assert.Equal("from claude", File.ReadAllText(Agents));
        Assert.Equal("from claude", File.ReadAllText(Claude));
    }

    /// <summary>An evaluation that queues behind the turnstile the open dialog holds and only starts
    /// after the workspace was unloaded must not act on it: with the generation sampled when the work
    /// starts rather than when it is queued, it would pass every IsCurrent check and leave a live
    /// mirror running on a workspace nobody has open.</summary>
    [Fact]
    public async Task An_evaluation_queued_behind_the_dialog_does_not_run_after_an_unload()
    {
        File.WriteAllText(Claude, "from claude");
        File.WriteAllText(Agents, "from agents");
        var coordinator = NewCoordinator();
        var wizardShown = new TaskCompletionSource();
        var releaseWizard = new TaskCompletionSource();
        coordinator.ShowWizard = async _ =>
        {
            wizardShown.TrySetResult();
            await releaseWizard.Task;
            return new AgentFileSyncWizardResult(true, AgentFileSyncEngine.ClaudeFileName);
        };

        var first = coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());
        await wizardShown.Task;
        var second = coordinator.EvaluateWorkspaceAsync(_dir, NoAgents());
        await Task.Delay(QuietMs); // let the second evaluation queue behind the dialog's turnstile
        coordinator.Unload(_dir);
        releaseWizard.TrySetResult();
        await Task.WhenAll(first, second);
        await Task.Delay(QuietMs);

        File.WriteAllText(Claude, "edited after the unload");
        await Task.Delay(QuietMs);

        Assert.Equal("from agents", File.ReadAllText(Agents));
    }
}
