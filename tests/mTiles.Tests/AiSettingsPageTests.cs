using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Shells;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The AI page: what typing into it actually stores, and what it refuses to store without asking.
/// </summary>
/// <remarks>Everything on this page could already be launched through and tested; none of it could be
/// typed anywhere, so the page is the whole of what these assertions are about.</remarks>
[Collection(ProviderSeamCollection.Name)]
public sealed class AiSettingsPageTests : IDisposable
{
    private readonly TempSettings _settings = new();

    // Saving a sign-in makes its directory, which is derived from AppPaths and not from TempSettings -
    // without this every run left agents/claude/<guid>/ inside the developer's own installation.
    private readonly TempAppData _appData = new();

    public void Dispose()
    {
        _settings.Dispose();
        _appData.Dispose();
    }

    private SettingsViewModel OnTheAiTab()
    {
        var vm = new SettingsViewModel(_settings.Service);
        vm.SelectTabCommand.Execute(SettingsTabs.Ai);
        return vm;
    }

    /// <summary>
    /// Every form actually opens.
    /// </summary>
    /// <remarks><b>Twenty-one tests drove these commands and none of them asserted this</b>, so a
    /// change that removed the <c>BeginEditing</c> call from the agent form passed the whole suite
    /// while the overlay never appeared and the two commands did nothing visible. Asserting the fields
    /// were filled is not the same as asserting the form is on screen — the fields are filled either
    /// way.</remarks>
    [Fact]
    public void Opening_a_form_shows_it()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "s1", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();

        vm.AddAgentInstanceCommand.Execute(null);
        Assert.True(vm.IsEditingAgentInstance);
        Assert.True(vm.IsEditingAnything);
        vm.CancelEditAgentInstanceCommand.Execute(null);
        Assert.False(vm.IsEditingAnything);

        vm.AddProviderInstanceCommand.Execute(null);
        Assert.True(vm.IsEditingProviderInstance);
        Assert.True(vm.IsEditingAnything);
        vm.CancelEditProviderInstanceCommand.Execute(null);

        vm.EditSignInCommand.Execute(vm.SignIns.Single());
        Assert.True(vm.IsEditingSignIn);
        Assert.True(vm.IsEditingAnything);
    }

    /// <summary>Reopening a saved instance shows it too — the path the tile chooser sends people down.
    /// </summary>
    [Fact]
    public void Reopening_an_instance_shows_the_form()
    {
        _settings.Service.Settings.AiAgentInstances.Add(
            new AiAgentInstance { Id = "a1", AgentId = "claude", Name = "Mine" });

        var vm = OnTheAiTab();
        vm.EditAgentInstanceCommand.Execute(vm.AgentInstances.Single(r => r.Name == "Mine"));

        Assert.True(vm.IsEditingAgentInstance);
        Assert.Equal("Mine", vm.EditAgentName);
    }

    /// <summary>
    /// Opening a form asks the account what it serves, with nothing on screen to trigger it.
    /// </summary>
    /// <remarks><b>It worked by accident.</b> <c>AccountChoice</c> is a record, so reopening an
    /// instance assigns the account it already names, raises no change and starts no fetch — while the
    /// suggestions have just been cleared. What refilled them was the combo writing a null back through
    /// its binding as its list was rebuilt, which is the trap <c>GoalTileViewModel</c> documents: the
    /// completion depended on a control being on screen, and the List button that used to be the manual
    /// way back is gone.</remarks>
    [Fact]
    public async Task Opening_a_form_asks_the_account_what_it_serves()
    {
        using var http = new StubModels("""{"data":[{"id":"z-ai/glm-5.3-flash"}]}""");
        _settings.Service.Settings.AiProviderInstances.Add(new AiProviderInstance
        {
            Id = "router", ProviderId = "openrouter", Name = "Work", ApiKey = "sk-test",
        });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            Id = "a1", AgentId = "claude", Name = "Mine", ApiAccountId = "router",
        });

        var vm = OnTheAiTab();
        var row = vm.AgentInstances.Single(r => r.Name == "Mine");

        // Twice, and the second time is the one that matters: the first open moves the account from
        // "the agent's own" to this one and would raise a change whatever this code did.
        vm.EditAgentInstanceCommand.Execute(row);
        await Suggested(vm);
        vm.CancelEditAgentInstanceCommand.Execute(null);

        vm.EditAgentInstanceCommand.Execute(row);
        Assert.Contains("z-ai/glm-5.3-flash", await Suggested(vm));
    }

    /// <summary>The suggestions, once the fetch behind them has answered.</summary>
    /// <remarks>Nothing here can await the command, so it waits for the answer rather than assuming a
    /// turn of the scheduler is enough.</remarks>
    private static async Task<IEnumerable<string>> Suggested(SettingsViewModel vm)
    {
        for (var i = 0; i < 200 && vm.ModelSuggestions.Count == 0; i++)
            await Task.Delay(10);

        return vm.ModelSuggestions;
    }

    /// <summary>One canned reply for whatever the provider layer asks, restored on disposal.</summary>
    private sealed class StubModels : IDisposable
    {
        public StubModels(string body) =>
            AiProvider.HandlerFactory = () => new CannedHandler(body);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class CannedHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }

    /// <summary>
    /// A pairing the launch would refuse is not offered by the chooser either.
    /// </summary>
    /// <remarks>"The chooser hides and the row explains" only holds while both read the same rule: pi
    /// on a local server was in the list, and the instance saved from it was unavailable the moment it
    /// existed.</remarks>
    [Fact]
    public void The_chooser_does_not_offer_a_pairing_the_launch_would_refuse()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "lmstudio", Name = "Mine" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);

        vm.EditAgentAgentName = "OpenCode";
        Assert.Contains(vm.AccountChoices, choice => choice.Id == "local");

        vm.EditAgentAgentName = "Pi Agent";
        Assert.DoesNotContain(vm.AccountChoices, choice => choice.Id == "local");
    }

    /// <summary>A provider typed in is a provider stored — the whole point of the page.</summary>
    [Fact]
    public void A_provider_can_be_added()
    {
        var vm = OnTheAiTab();
        vm.AddProviderInstanceCommand.Execute(null);
        vm.EditProviderName = "Work";
        vm.EditProviderKind = "OpenRouter";
        vm.EditProviderApiKey = "sk-test";
        vm.SaveProviderInstanceCommand.Execute(null);

        var stored = Assert.Single(_settings.Service.Settings.AiProviderInstances);
        Assert.Equal("Work", stored.Name);
        Assert.Equal("openrouter", stored.ProviderId);
        Assert.Equal("sk-test", stored.ApiKey);
        Assert.False(vm.IsEditingAnything);
    }

    /// <summary>
    /// A new provider starts unnamed, and cannot be saved until somebody names it.
    /// </summary>
    /// <remarks>It used to open holding the first provider's display name. Nothing rewrites that field
    /// when Service changes — nothing can tell a default from a deliberate answer that matches it — so
    /// picking LM Studio left a row called "Anthropic" pointing at a local server, and the account
    /// chooser then identified it by that name.</remarks>
    [Fact]
    public void A_new_provider_is_unnamed_and_cannot_be_saved_until_it_is_named()
    {
        var vm = OnTheAiTab();
        vm.AddProviderInstanceCommand.Execute(null);

        Assert.Equal("", vm.EditProviderName);
        Assert.False(vm.CanSaveProviderInstance);

        vm.SaveProviderInstanceCommand.Execute(null);
        Assert.True(vm.IsEditingProviderInstance);
        Assert.Empty(_settings.Service.Settings.AiProviderInstances);

        vm.EditProviderName = "my lm studio";
        Assert.True(vm.CanSaveProviderInstance);

        vm.SaveProviderInstanceCommand.Execute(null);
        Assert.Equal("my lm studio", Assert.Single(_settings.Service.Settings.AiProviderInstances).Name);
    }

    /// <summary>Whitespace is not a name.</summary>
    [Fact]
    public void A_provider_named_only_with_spaces_cannot_be_saved()
    {
        var vm = OnTheAiTab();
        vm.AddProviderInstanceCommand.Execute(null);
        vm.EditProviderName = "   ";

        Assert.False(vm.CanSaveProviderInstance);
    }

    /// <summary>
    /// What hides an instance and what explains it are the same sentence.
    /// </summary>
    /// <remarks>They had drifted: a deleted sign-in made the instance vanish from every chooser while
    /// its row said nothing, and pi on a local server was offered everywhere and refused only at
    /// startup. The division of labour is the chooser hides and the row explains, which only works
    /// while both read <c>AgentAvailability</c>.</remarks>
    [Fact]
    public void An_unavailable_instance_always_has_a_row_that_says_why()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "lmstudio", Name = "Mine" });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            Id = "pi-local", AgentId = "pi", Name = "Pi local", ApiAccountId = "local",
        });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            Id = "orphan", AgentId = "claude", Name = "Orphan", SignInId = "gone",
        });

        var vm = OnTheAiTab();

        foreach (var name in new[] { "Pi local", "Orphan" })
        {
            var row = vm.AgentInstances.Single(r => r.Name == name);
            Assert.True(row.IsUnavailable, $"{name} is unavailable and its row says nothing");
            Assert.NotEmpty(row.UnavailableNote);
        }
    }

    /// <summary>A sign-in needs a name too — the rule the other two forms already had.</summary>
    [Fact]
    public void A_new_sign_in_cannot_be_saved_unnamed()
    {
        var vm = OnTheAiTab();
        vm.AddSignInCommand.Execute(null);

        Assert.False(vm.CanSaveSignIn);
        vm.SaveSignInCommand.Execute(null);
        Assert.Empty(_settings.Service.Settings.AiSignIns);

        vm.EditSignInName = "Work";
        Assert.True(vm.CanSaveSignIn);
    }

    /// <summary>
    /// A sign-in cannot be saved without a tool either, and that is the more expensive half.
    /// </summary>
    /// <remarks>The tool is fixed once the row exists, so one saved without it is a row that can only
    /// be deleted: no agent, no Sign in button, and a directory under a placeholder name. The empty
    /// selection is what a <c>ComboBox</c> writes when its <c>ItemsSource</c> is rebuilt under it,
    /// which is why this is a rule and not an assumption about the form.</remarks>
    [Fact]
    public void A_sign_in_cannot_be_saved_without_a_tool()
    {
        var vm = OnTheAiTab();
        vm.AddSignInCommand.Execute(null);
        vm.EditSignInName = "Work";

        // Opening the form chooses one, and the list it comes from holds it.
        Assert.NotEmpty(vm.EditSignInAgentName);
        Assert.Contains(vm.EditSignInAgentName, vm.SignInAgentChoices);
        Assert.True(vm.CanSaveSignIn);

        vm.EditSignInAgentName = "";
        Assert.False(vm.CanSaveSignIn);
        vm.SaveSignInCommand.Execute(null);
        Assert.Empty(_settings.Service.Settings.AiSignIns);

        vm.EditSignInAgentName = AiAgentCatalog.All[0].DisplayName;
        vm.SaveSignInCommand.Execute(null);
        Assert.NotEmpty(_settings.Service.Settings.AiSignIns.Single().AgentId);
    }

    /// <summary>
    /// Sign in opens a tile that actually runs the CLI, pointed at this sign-in's directory.
    /// </summary>
    /// <remarks><b>The one path of this feature nothing asserted, and the one that was broken.</b> The
    /// plan is run through <c>InstallCommand.For</c>; it used to be run through
    /// <c>InstallPlan.CommandLine</c>, whose quoting is for reading — and since every part of a shell
    /// line contains a space, the tile was handed the whole command inside quotes and printed it
    /// instead. The directory was made, the row went on saying "not signed in", and nothing said
    /// why.</remarks>
    [Fact]
    public void Signing_in_runs_the_tool_with_its_directory_set()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "s1", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();
        InstallPlan? asked = null;
        // Agreed to, because what the tile is for is now in the question - see
        // Opening_a_tile_for_a_plan_says_what_it_is_for_first.
        vm.ConfirmAction = _ => Task.FromResult(true);
        vm.RunInstallPlan = plan =>
        {
            asked = plan;
            return Task.FromResult(true);
        };

        vm.SignInCommand.Execute(vm.SignIns.Single());

        var shell = ShellTerminalCatalog.ResolveDefault(_settings.Service.Settings).Shell;
        var command = InstallCommand.For(asked!, shell);

        // Two variables set and the binary run, in one line the shell will execute rather than echo.
        Assert.Contains("CLAUDE_CONFIG_DIR", command);
        Assert.Contains(AiSignInStore.DirectoryFor(vm.SignIns.Single().SignIn), command);
        Assert.EndsWith("claude", command);
        Assert.DoesNotContain("\"$env", command);
        Assert.False(command.StartsWith('"'), $"the whole command is quoted: {command}");

        // And the readable form is *not* that, which is the whole reason the two are separate: it
        // quotes every part with a space in it, and here that is the entire command.
        Assert.StartsWith("\"", asked!.CommandLine);
    }

    /// <summary>
    /// The exact line each shell is given, spelled out rather than recomputed.
    /// </summary>
    /// <remarks><b>This test used to build its expectation with the same expression the method uses</b>
    /// — quote every part and join — so it agreed with any quoting at all, including one no shell will
    /// run. That is how <c>'npm' 'install' -g …</c> passed green while PowerShell answered
    /// <c>Unexpected token</c> before starting anything. Written out per shell, it can disagree.
    /// </remarks>
    [Fact]
    public void An_install_plan_reaches_the_tile_as_a_line_the_shell_will_run()
    {
        var plan = new InstallPlan("npm", ["install", "-g", "@anthropic-ai/claude-code"], "");

        // The call operator, because PowerShell reads a quoted first token as a string expression.
        Assert.Equal("& 'npm' 'install' '-g' '@anthropic-ai/claude-code'",
            InstallCommand.For(plan, new PowerShellTerminal()));

        Assert.Equal("'npm' 'install' '-g' '@anthropic-ai/claude-code'",
            InstallCommand.For(plan, new BashTerminal()));

        // A plan carrying a whole composed line - which is what the Sign in button makes - is not
        // touched at all: quoting it would be quoting a command.
        var composed = new InstallPlan("""$env:CLAUDE_CONFIG_DIR = 'C:\dir'; claude""", [], "");
        Assert.Equal(composed.Executable, InstallCommand.For(composed, new PowerShellTerminal()));
    }

    /// <summary>
    /// Renaming a sign-in for an agent this build does not have does not move it to another tool.
    /// </summary>
    /// <remarks><b>Asserting the control was disabled was not the same as asserting nothing changed.</b>
    /// The form showed the first installed tool for an unresolvable agent id — silently, because the
    /// field is disabled for a stored row — and Save wrote it. The directory is composed from the
    /// agent's id and the sign-in's, so the refresh token was left in the old one and the row pointed
    /// at an empty directory belonging to a different CLI. This is precisely the row
    /// <c>AgentAvailability</c> keeps reachable after a Velopack rollback so that it can be renamed or
    /// removed.</remarks>
    [Fact]
    public void Renaming_a_sign_in_for_an_unknown_agent_keeps_its_tool()
    {
        _settings.Service.Settings.AiSignIns.Add(new AiSignIn
        {
            Id = "s1", AgentId = "from-a-newer-build", Name = "Work",
        });

        var vm = OnTheAiTab();
        var before = AiSignInStore.DirectoryFor(vm.SignIns.Single().SignIn);
        vm.EditSignInCommand.Execute(vm.SignIns.Single());

        // The field says what it stores rather than the first tool that happens to be installed.
        Assert.False(vm.CanChooseSignInAgent);
        Assert.Equal("from-a-newer-build", vm.EditSignInAgentName);

        // And it can still be renamed, which is the whole reason the row is reachable.
        vm.EditSignInName = "Work laptop";
        Assert.True(vm.CanSaveSignIn);
        vm.SaveSignInCommand.Execute(null);

        var stored = Assert.Single(_settings.Service.Settings.AiSignIns);
        Assert.Equal("Work laptop", stored.Name);
        Assert.Equal("from-a-newer-build", stored.AgentId);
        Assert.Equal(before, AiSignInStore.DirectoryFor(stored));
    }

    /// <summary>
    /// Both routes to a tile put what it is for in the question.
    /// </summary>
    /// <remarks>The plan's note carries the only sentence saying what to do once the tile is open — for
    /// a sign-in, that the tool's own login command has to be typed — and the route a plan takes to a
    /// tile carries the command alone. Built and discarded, it left a tile with the environment set, an
    /// empty prompt and a row still saying "not signed in".</remarks>
    [Fact]
    public void Opening_a_tile_for_a_plan_says_what_it_is_for_first()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "s1", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();
        var asked = new List<string>();
        vm.ConfirmAction = question =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        vm.RunInstallPlan = _ => Task.FromResult(true);

        vm.SignInCommand.Execute(vm.SignIns.Single());

        var question = Assert.Single(asked);
        Assert.Contains("login command", question);
        Assert.Contains(AiSignInStore.DirectoryFor(vm.SignIns.Single().SignIn), question);
    }

    /// <summary>Refusing it opens nothing.</summary>
    [Fact]
    public void A_refused_sign_in_opens_no_tile()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "s1", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();
        vm.ConfirmAction = _ => Task.FromResult(false);
        var opened = false;
        vm.RunInstallPlan = _ =>
        {
            opened = true;
            return Task.FromResult(true);
        };

        vm.SignInCommand.Execute(vm.SignIns.Single());

        Assert.False(opened);
    }

    /// <summary>
    /// Opening the form of an instance the row calls unavailable does not throw its account away.
    /// </summary>
    /// <remarks><b>The row explaining the problem was the form that destroyed the evidence.</b> The
    /// chooser lists what can be chosen now, so an account that is deleted, belongs to another tool or
    /// is one this agent cannot speak to is absent from it — and restoring the selection then fell to
    /// "the agent's own account", which the next Save wrote. A rename was enough: a different
    /// subscription, silently, with nothing left saying what had been configured.</remarks>
    [Theory]
    // A provider this agent cannot be pointed at - the pairing the row's chip is about.
    [InlineData("local", "pi", "local")]
    // A sign-in that has been removed: nothing to look up, and the id still has to survive.
    [InlineData("", "claude", "gone")]
    public void Opening_an_unavailable_instance_keeps_the_account_it_stores(
        string providerId, string agentId, string accountId)
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "lmstudio", Name = "Mine" });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            Id = "a1", AgentId = agentId, Name = "Mine",
            ApiAccountId = providerId.Length > 0 ? accountId : "",
            SignInId = providerId.Length > 0 ? "" : accountId,
        });

        var vm = OnTheAiTab();
        var row = vm.AgentInstances.Single(r => r.Instance.Id == "a1");
        Assert.True(row.IsUnavailable);

        vm.EditAgentInstanceCommand.Execute(row);

        // Selected, and shown for what it is rather than as a working answer.
        Assert.Equal(accountId, vm.EditAgentAccount?.Id);
        Assert.Contains(vm.AccountChoices, choice => choice.Id == accountId);

        vm.EditAgentName = "Renamed";
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances.Single(i => i.Id == "a1");
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(accountId, providerId.Length > 0 ? stored.ApiAccountId : stored.SignInId);
    }

    /// <summary>
    /// The account field keeps its old name on disk, whatever it is called in the code.
    /// </summary>
    /// <remarks><b>A rename here is a migration nobody would notice.</b> The property became
    /// <c>ApiAccountId</c> when a provider stopped being the only kind of account, and the JSON name
    /// stayed <c>ProviderInstanceId</c> so that a build Velopack has rolled back reads the same file —
    /// and so that this one reads what that build wrote. Dropped, every instance would silently come
    /// back on the CLI's own account: a different subscription billed, with nothing on screen saying
    /// so.</remarks>
    [Fact]
    public void The_account_field_is_still_called_ProviderInstanceId_on_disk()
    {
        var read = System.Text.Json.JsonSerializer.Deserialize<AiAgentInstance>(
            """{"Id":"a1","AgentId":"claude","ProviderInstanceId":"router"}""",
            JsonDefaults.SettingsOptions);

        Assert.Equal("router", read!.ApiAccountId);

        var written = System.Text.Json.JsonSerializer.Serialize(
            new AiAgentInstance { Id = "a1", AgentId = "claude", ApiAccountId = "router" },
            JsonDefaults.SettingsOptions);

        Assert.Contains("\"ProviderInstanceId\"", written);
        Assert.DoesNotContain("ApiAccountId", written);
    }

    /// <summary>
    /// A late answer does not land on a form that has moved on.
    /// </summary>
    /// <remarks>The declared purpose of the cancellation in <c>LoadAgentModelsAsync</c>, and the thing
    /// no test exercised: two quick changes of account left the slower reply overwriting
    /// <c>ModelSuggestions</c> for an account nobody has selected — and <c>RefreshEffortLabels</c> then
    /// narrowing the effort levels by a stranger's model list. The regression is silent: the wrong
    /// list, and no exception anywhere.</remarks>
    [Fact]
    public async Task A_slower_answer_does_not_overwrite_a_newer_choice()
    {
        using var http = new SlowThenFast(
            """{"data":[{"id":"slow/model"}]}""", """{"data":[{"id":"fast/model"}]}""");

        _settings.Service.Settings.AiProviderInstances.Add(new AiProviderInstance
        {
            Id = "slow", ProviderId = "openrouter", Name = "Slow", ApiKey = "sk-test",
        });
        _settings.Service.Settings.AiProviderInstances.Add(new AiProviderInstance
        {
            Id = "fast", ProviderId = "openrouter", Name = "Fast", ApiKey = "sk-test",
        });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";

        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "slow");
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "fast");

        for (var i = 0; i < 200 && vm.ModelSuggestions.Count == 0; i++)
            await Task.Delay(10);

        // Long enough for the first reply to have arrived and overwritten this one, if it could.
        await Task.Delay(200);

        Assert.Contains("fast/model", vm.ModelSuggestions);
        Assert.DoesNotContain("slow/model", vm.ModelSuggestions);
    }

    /// <summary>The first request is held back; every later one answers at once.</summary>
    /// <remarks>Which is what a second choice made while the first is still in flight looks like, and
    /// the only ordering under which the bug this pins can happen at all.</remarks>
    private sealed class SlowThenFast : IDisposable
    {
        /// <summary>Shared by every <see cref="Handler"/> the factory builds, because
        /// <c>AiProvider.ClientFor</c> makes a fresh handler per HTTP call — per-instance counters
        /// would answer "first" to both requests and both would take the slow path.</summary>
        private readonly int[] _answered = new int[1];

        public SlowThenFast(string first, string rest) =>
            AiProvider.HandlerFactory = () => new Handler(first, rest, _answered);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class Handler(string first, string rest, int[] answered) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var isFirst = Interlocked.Increment(ref answered[0]) == 1;
                if (isFirst) await Task.Delay(150, cancellationToken);

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(isFirst ? first : rest, System.Text.Encoding.UTF8,
                        "application/json"),
                };
            }
        }
    }

    /// <summary>The tool is chosen once and then fixed, because the directory is named after the
    /// sign-in rather than the tool.</summary>
    [Fact]
    public void A_saved_sign_in_cannot_change_tool()
    {
        var vm = OnTheAiTab();
        vm.AddSignInCommand.Execute(null);
        Assert.True(vm.CanChooseSignInAgent);

        vm.EditSignInName = "Work";
        vm.SaveSignInCommand.Execute(null);

        var row = vm.SignIns.Single(r => r.Name == "Work");
        vm.EditSignInCommand.Execute(row);

        Assert.False(vm.CanChooseSignInAgent);
    }

    /// <summary>An agent instance points at a provider the list shows, and stores its id.</summary>
    [Fact]
    public void An_agent_instance_is_pointed_at_a_provider()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentName = "Claude on OpenRouter";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "p1");
        vm.EditAgentModel = "some/model";
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances[^1];
        Assert.Equal("Claude on OpenRouter", stored.Name);
        Assert.Equal("p1", stored.ApiAccountId);
        Assert.Equal("some/model", stored.Model);
    }

    /// <summary>
    /// Reopening an instance shows the account it was saved with, not an empty box.
    /// </summary>
    /// <remarks>The form used to build the account list before it knew which agent it was editing, then
    /// assign the selection, and only then let the agent's own change handler rebuild the list —
    /// clearing the combo, which writes its null selection straight back. The instance kept its stored
    /// account; the form simply stopped showing it, which is worse than losing it, because the next
    /// Save writes back whatever the blank box resolves to.</remarks>
    [Fact]
    public void Reopening_an_instance_shows_the_account_it_was_saved_with()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";
        vm.EditAgentName = "Claude on OpenRouter";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "p1");
        vm.SaveAgentInstanceCommand.Execute(null);

        // Opening another form in between is what leaves the previous agent's list behind.
        vm.AddAgentInstanceCommand.Execute(null);
        vm.CancelEditAgentInstanceCommand.Execute(null);

        var row = vm.AgentInstances.Single(r => r.Name == "Claude on OpenRouter");
        vm.EditAgentInstanceCommand.Execute(row);

        Assert.Equal("p1", vm.EditAgentAccount?.Id);
        Assert.Contains(vm.AccountChoices, choice => choice.Id == "p1");
    }

    /// <summary>
    /// An agent with nowhere to authenticate but its own account says so, and says where to fix it.
    /// </summary>
    /// <remarks>A one-entry chooser looks identical to one whose other entries are hidden for a reason
    /// nobody can see — which is exactly what happens, because an agent is only offered providers whose
    /// API it can speak.</remarks>
    [Fact]
    public void An_agent_with_no_usable_account_says_where_to_add_one()
    {
        // Ollama serves no Anthropic-shaped endpoint - its /v1/messages is a 404 - so Claude Code
        // cannot be pointed at it. LM Studio used to be the example here and stopped being one the day
        // it was measured to serve /v1/messages properly, which is the flavors doing their job.
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Mine" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";

        Assert.True(vm.HasNoAccountToChoose);
        Assert.Contains("Providers", vm.NoAccountNote);

        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "router", ProviderId = "openrouter", Name = "Work" });
        vm.CancelEditAgentInstanceCommand.Execute(null);

        var reopened = OnTheAiTab();
        reopened.AddAgentInstanceCommand.Execute(null);
        reopened.EditAgentAgentName = "Claude Code";

        Assert.False(reopened.HasNoAccountToChoose);
    }

    /// <summary>
    /// A new agent instance starts unnamed and cannot be saved until somebody names it.
    /// </summary>
    /// <remarks>Same rule as the provider form, same reason: the name is what the tile chooser and the
    /// Goal tile's list identify the row by, and a seeded one meant a second instance of an agent
    /// arrived spelled exactly like the first.</remarks>
    [Fact]
    public void A_new_agent_instance_is_unnamed_and_cannot_be_saved_until_it_is_named()
    {
        var vm = OnTheAiTab();
        var before = _settings.Service.Settings.AiAgentInstances.Count;
        vm.AddAgentInstanceCommand.Execute(null);

        Assert.Equal("", vm.EditAgentName);
        Assert.False(vm.CanSaveAgentInstance);

        vm.SaveAgentInstanceCommand.Execute(null);
        Assert.Equal(before, _settings.Service.Settings.AiAgentInstances.Count);

        vm.EditAgentName = "Claude on OpenRouter";
        Assert.True(vm.CanSaveAgentInstance);

        vm.SaveAgentInstanceCommand.Execute(null);
        Assert.Equal("Claude on OpenRouter",
            _settings.Service.Settings.AiAgentInstances[^1].Name);
    }

    /// <summary>Two providers named alike are still two providers: the one that was chosen is the one
    /// stored, and the one the form shows when it is reopened.</summary>
    /// <remarks>Nothing makes an instance's name unique — a new one is seeded with the provider's own
    /// display name — so two keys for the same service are two identically spelled rows. Keyed by name,
    /// both the save and the reopen answered with the first of them, and the agent authenticated as the
    /// wrong account with nothing on screen saying so.</remarks>
    [Fact]
    public void Two_providers_with_the_same_name_are_told_apart()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "OpenRouter" });
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p2", ProviderId = "openrouter", Name = "OpenRouter" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentName = "On the second key";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "p2");
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances[^1];
        Assert.Equal("p2", stored.ApiAccountId);

        var row = vm.AgentInstances.Single(r => r.Name == "On the second key");
        vm.EditAgentInstanceCommand.Execute(row);
        Assert.Equal("p2", vm.EditAgentAccount?.Id);
    }

    /// <summary>A provider the agent cannot speak to is not offered at all.</summary>
    /// <remarks>Stored, the pairing makes the instance unavailable everywhere — gone from the Agent
    /// tile's chooser and from the Goal tile's list — so offering it here is offering a configuration
    /// that stops working the moment it is saved.</remarks>
    [Fact]
    public void An_incompatible_provider_is_not_offered()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "router", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Codex";

        // Codex speaks /v1/responses; Ollama serves /v1/chat/completions.
        Assert.DoesNotContain(vm.AccountChoices, choice => choice.Id == "local");
        Assert.Contains(vm.AccountChoices, choice => choice.Id == "router");
    }

    /// <summary>
    /// Changing the agent keeps an account that both agents can use.
    /// </summary>
    /// <remarks><b>There was a test for losing an incompatible account and none for keeping a
    /// compatible one</b>, so the case that mattered went unpinned: rebuilding the list clears the
    /// combo's SelectedItem and the binding writes that null back, and the selection was only restored
    /// on the branch that had lost it. Switching between two agents that both speak to OpenRouter
    /// emptied the field, and the next Save stored "the agent's own account".</remarks>
    [Fact]
    public void Changing_the_agent_keeps_an_account_both_can_use()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "router", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "router");

        vm.EditAgentAgentName = "Codex";

        Assert.Equal("router", vm.EditAgentAccount?.Id);
    }

    /// <summary>Choosing an agent that cannot use the provider already selected clears the choice
    /// rather than leaving a pairing the chooser no longer shows.</summary>
    [Fact]
    public void Changing_the_agent_drops_a_provider_it_cannot_use()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "OpenCode";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "local");

        vm.EditAgentAgentName = "Codex";

        Assert.Equal(AccountChoice.Default, vm.EditAgentAccount);
    }

    /// <summary>
    /// Choosing a subscription stores a sign-in and clears the provider, and choosing a provider does
    /// the reverse.
    /// </summary>
    /// <remarks>They are one chooser precisely so that an instance can never carry both, and this is
    /// what makes that true rather than merely intended: the combination points the CLI at one
    /// account's directory while authenticating with another's key, so the work is billed to the
    /// provider while every row on screen names the subscription.</remarks>
    [Fact]
    public void An_account_is_stored_as_a_sign_in_or_a_provider_but_never_both()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "Work" });
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "s1", AgentId = "claude", Name = "Personal" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";
        vm.EditAgentName = "On the second subscription";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "s1");
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances[^1];
        Assert.Equal("s1", stored.SignInId);
        Assert.Equal("", stored.ApiAccountId);

        // And back the other way, on the same instance, so the clearing is tested and not just the
        // writing: an instance edited from a subscription to a key must stop being on the subscription.
        var row = vm.AgentInstances.Single(r => r.Name == "On the second subscription");
        vm.EditAgentInstanceCommand.Execute(row);
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "p1");
        vm.SaveAgentInstanceCommand.Execute(null);

        Assert.Equal("p1", stored.ApiAccountId);
        Assert.Equal("", stored.SignInId);
    }

    /// <summary>Another agent's sign-in is not offered, for the reason its provider would not be.
    /// </summary>
    /// <remarks>A login is one CLI's: codex keeps its credentials in another file under another
    /// variable, so a Claude Code account offered to it is a pairing that cannot work.</remarks>
    [Fact]
    public void A_sign_in_belonging_to_another_agent_is_not_offered()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "claude-work", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";
        Assert.Contains(vm.AccountChoices, choice => choice.Id == "claude-work");

        vm.EditAgentAgentName = "Codex";
        Assert.DoesNotContain(vm.AccountChoices, choice => choice.Id == "claude-work");
    }

    /// <summary>And changing the agent drops a sign-in it cannot use, as it drops a provider.</summary>
    [Fact]
    public void Changing_the_agent_drops_a_sign_in_it_cannot_use()
    {
        _settings.Service.Settings.AiSignIns.Add(
            new AiSignIn { Id = "claude-work", AgentId = "claude", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Claude Code";
        vm.EditAgentAccount = vm.AccountChoices.Single(choice => choice.Id == "claude-work");

        vm.EditAgentAgentName = "Codex";

        Assert.Equal(AccountChoice.Default, vm.EditAgentAccount);
    }

    /// <summary>A stored pairing that cannot work says so on its row — the only place that can.</summary>
    [Fact]
    public void An_incompatible_instance_says_why_it_is_not_offered()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            AgentId = "codex", Name = "Codex on Ollama", ApiAccountId = "local",
        });

        var vm = OnTheAiTab();

        var row = vm.AgentInstances.Single(r => r.Name == "Codex on Ollama");
        Assert.True(row.IsUnavailable);
        Assert.Contains("Ollama", row.UnavailableNote);
    }

    /// <summary>The effort chooser offers what the agent accepts, and a level it does not falls back
    /// to the tool's own default rather than staying selected in a list that no longer holds it.
    /// </summary>
    [Fact]
    public void The_effort_chooser_follows_the_agent()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentEffort = AiEfforts.Label(AiEffort.High);

        // Measured: opencode's effort is not something the CLI takes at all.
        vm.EditAgentAgentName = "OpenCode";

        Assert.Equal([AiEfforts.Label(AiEffort.ToolDefault)], vm.EffortLabels);
        Assert.Equal(AiEfforts.Label(AiEffort.ToolDefault), vm.EditAgentEffort);
    }

    /// <summary>The behaviour chooser offers what the agent has a gate for, and a mode it has none for
    /// falls back to the tool's own default.</summary>
    /// <remarks>Offering <c>plan</c> for an agent that cannot plan is a row promising a restriction
    /// that does not exist: it is stored, rounded away, and the agent runs unrestricted anyway.
    /// </remarks>
    [Fact]
    public void The_behaviour_chooser_follows_the_agent()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.Plan);

        // Measured: pi has no permission gate at all, so only bypass and the tool's default are real.
        vm.EditAgentAgentName = "Pi Agent";

        Assert.Equal(
            [AiBehaviours.Label(AiBehaviour.BypassPermissions),
             AiBehaviours.Label(AiBehaviour.ToolDefault)],
            vm.BehaviourLabels);
        Assert.Equal(AiBehaviours.Label(AiBehaviour.ToolDefault), vm.EditAgentBehaviour);
    }

    /// <summary>
    /// Turning every safeguard off is asked about, and an unwired dialog answers no.
    /// </summary>
    /// <remarks>The same rule the Goal tile's strip follows, and for the same reason: it applies
    /// wherever the instance is used, and the first place it is noticed is a run that has already
    /// happened.</remarks>
    [Fact]
    public void Bypass_is_not_stored_without_an_answer()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.BypassPermissions);
        vm.SaveAgentInstanceCommand.Execute(null);

        Assert.DoesNotContain(_settings.Service.Settings.AiAgentInstances,
            instance => instance.DefaultBehaviour == AiBehaviour.BypassPermissions);
    }

    /// <summary>Agreeing stores it — or the question above would be a refusal dressed as one.</summary>
    [Fact]
    public void Bypass_is_stored_when_it_is_agreed_to()
    {
        var vm = OnTheAiTab();
        vm.ConfirmAction = _ => Task.FromResult(true);
        vm.AddAgentInstanceCommand.Execute(null);
        // A name, because the form now requires one - Save is refused without it.
        vm.EditAgentName = "Unattended";
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.BypassPermissions);
        vm.SaveAgentInstanceCommand.Execute(null);

        Assert.Equal(AiBehaviour.BypassPermissions,
            _settings.Service.Settings.AiAgentInstances[^1].DefaultBehaviour);
    }

    /// <summary>Deleting is destructive, so an unwired dialog answers no here too.</summary>
    [Fact]
    public void Nothing_is_deleted_without_an_answer()
    {
        _settings.Service.Settings.AiAgentInstances.Add(
            new AiAgentInstance { AgentId = AiAgentCatalog.All[0].Id, Name = "Mine" });

        var vm = OnTheAiTab();
        var row = vm.AgentInstances.Single(r => r.Name == "Mine");
        vm.DeleteAgentInstanceCommand.Execute(row);

        Assert.Contains(_settings.Service.Settings.AiAgentInstances,
            instance => instance.Name == "Mine");
    }

    /// <summary>Every seeded instance has a row, so a machine that has never been in here still shows
    /// what it can run.</summary>
    [Fact]
    public void The_seeded_instances_are_listed()
    {
        // Seeded by the settings service itself, so this is the state a first run opens the page in.
        var rows = OnTheAiTab().AgentInstances;

        Assert.Equal(AiAgentCatalog.All.Count, rows.Count);
        foreach (var agent in AiAgentCatalog.All)
            Assert.Contains(rows, row => row.AgentName == agent.DisplayName);
    }
}
