using System.Text.Json.Nodes;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;
using mTiles.Services.Tiles;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Which account a conversation ran as: asked about before it moves, said on screen where it did, and put
/// back when the conversation is opened again.
/// </summary>
/// <remarks>
/// The transcript is this application's and survives every switch; the resume token is the CLI's and lives
/// in the account's own directory, so a second subscription resumes nothing. Everything here exists because
/// that loss used to arrive as a notice after the new session had already started.
/// </remarks>
public class ConversationAccountTests
{
    [Fact]
    public async Task Moving_to_another_login_of_the_same_agent_asks_first()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var elsewhere = Beside(settings, here, signIn: "second-subscription");
        using var tile = await StartedWithASession(settings, here);

        var asked = 0;
        tile.ConfirmAction = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };

        await tile.SwitchInstanceAsync(elsewhere);

        Assert.Equal(1, asked);
        Assert.Equal(here.Id, tile.Instance.Id);

        tile.ConfirmAction = _ => Task.FromResult(true);
        await tile.SwitchInstanceAsync(elsewhere);

        Assert.Equal(elsewhere.Id, tile.Instance.Id);
    }

    [Fact]
    public async Task Moving_to_another_login_of_the_same_agent_hands_the_work_over()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var elsewhere = Beside(settings, here, signIn: "second-subscription");
        var store = TestTiles.ConversationStore();
        using var tile = await StartedWithASession(settings, here, store);
        store.Append(tile.ConversationId, [new UserMessageAdded("m1", "fix the build", [])]);
        tile.ConfirmAction = _ => Task.FromResult(true);

        await tile.SwitchInstanceAsync(elsewhere);

        // The token lives in the login's own directory, so the arriving session could resume nothing.
        Assert.Null(store.Find(tile.ConversationId)?.ResumeToken);
        Assert.Contains(store.ReadEvents(tile.ConversationId), e => e is HandoverRecorded);
    }

    [Fact]
    public async Task A_conversation_with_nothing_to_resume_changes_account_without_asking()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var elsewhere = Beside(settings, here, signIn: "second-subscription");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });
        tile.ConfirmAction = _ => throw new InvalidOperationException("No session, so nothing is lost.");

        await tile.SwitchInstanceAsync(elsewhere);

        Assert.Equal(elsewhere.Id, tile.Instance.Id);
    }

    [Fact]
    public async Task Another_row_on_the_same_login_is_not_worth_a_dialog()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var sameAccount = Beside(settings, here, signIn: here.SignInId);
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });
        tile.ConfirmAction = _ => throw new InvalidOperationException("Nothing is lost, so nothing is asked.");

        await tile.SwitchInstanceAsync(sameAccount);

        Assert.Equal(sameAccount.Id, tile.Instance.Id);
    }

    [Fact]
    public void Opening_a_conversation_puts_back_the_model_mode_and_effort_it_last_ran_on()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());

        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("opus", "Plan", "token", "Max"),
            new SessionModelChosen("opus"),
        ]), tile.Agent);

        Assert.Equal(new SessionOverrides("opus", AiBehaviour.Plan, AiEffort.Max), tile.Overrides);
    }

    [Fact]
    public void A_model_the_session_only_reported_running_is_not_pinned_as_an_override()
    {
        using var settings = new TempSettings();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        instance.Model = "";
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });

        // Claude Code reports the id it resolved the CLI's default to; written down, it would freeze that
        // default in this tile over every later change of the instance's model in Settings.
        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("claude-opus-5", null, "token"),
        ]), tile.Agent);

        Assert.Null(tile.Overrides.Model);
    }

    [Fact]
    public void A_restart_leaves_what_the_session_reported_to_the_instance()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());

        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("opus", "Plan", "token", "Max"),
        ]), tile.Agent, settingsToo: false);

        Assert.Equal(SessionOverrides.None, tile.Overrides);
    }

    [Fact]
    public void What_the_instance_answers_by_itself_is_not_pinned_as_an_override()
    {
        using var settings = new TempSettings();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        instance.Model = "opus";
        instance.DefaultBehaviour = AiBehaviour.Plan;
        instance.DefaultEffort = AiEffort.Max;
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });

        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("opus", "Plan", "token", "Max"),
        ]), tile.Agent);

        Assert.Equal(SessionOverrides.None, tile.Overrides);
    }

    [Fact]
    public void A_model_resolved_at_every_launch_is_never_written_down()
    {
        using var settings = new TempSettings();
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        instance.Model = AiModelChoice.FirstLoaded;
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });

        tile.AdoptStoredSession(ConversationReducer.Replay([new SessionConfigured("qwen3", null, "token")]),
            tile.Agent);

        Assert.Null(tile.Overrides.Model);
    }

    [Fact]
    public void Opening_a_conversation_never_puts_the_tile_into_bypass()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());

        // Adoption is a start nobody is standing in front of, and bypass is the one grant the strip only
        // reaches through a dialog — restored here it would arm the tile silently and survive every restart.
        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("opus", SessionSettingOptions.ModeId(AiBehaviour.BypassPermissions), null,
                "Max"),
        ]), tile.Agent);

        Assert.Null(tile.Overrides.Behaviour);
        Assert.Equal(AiEffort.Max, tile.Overrides.Effort);
    }

    [Fact]
    public void What_the_tile_already_overrides_outranks_what_the_conversation_last_ran_on()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());
        tile.AdoptStoredSession(ConversationReducer.Replay([
            // Accept edits rather than auto: a seeded instance starts on auto now, and an adopted mode equal to
            // the instance's own is no override at all.
            new SessionConfigured("sonnet", SessionSettingOptions.ModeId(AiBehaviour.AcceptEdits), null),
            new SessionModelChosen("sonnet"),
        ]), tile.Agent);

        tile.AdoptStoredSession(ConversationReducer.Replay([
            new SessionConfigured("opus", "Plan", null, "Max"), new SessionModelChosen("opus"),
        ]), tile.Agent);

        Assert.Equal(new SessionOverrides("sonnet", AiBehaviour.AcceptEdits, AiEffort.Max), tile.Overrides);
    }

    [Fact]
    public void A_restored_model_is_spelled_the_way_the_instance_stores_it_and_not_the_way_the_session_said_it()
    {
        using var settings = new TempSettings();
        var provider = new AiProviderInstance { ProviderId = "openrouter", ApiKey = "sk-test" };
        settings.Service.Settings.AiProviderInstances.Add(provider);
        var instance = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "opencode");
        instance.ApiAccountId = provider.Id;
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id });

        // What an opencode session lists is already qualified with its registry's provider name; kept as it
        // stands, the next launch would qualify it again into openrouter/openrouter/auto.
        tile.AdoptStoredSession(
            ConversationReducer.Replay([new SessionModelChosen("openrouter/openrouter/auto")]),
            tile.Agent);

        Assert.Equal("openrouter/auto", tile.Overrides.Model);
        Assert.Equal("openrouter/openrouter/auto", tile.Agent.QualifiedModel(
            AgentRuntime.For(settings.Service.Settings, tile.Overrides.ApplyTo(instance), agent: tile.Agent)));
    }

    [Fact]
    public void The_entry_that_begins_a_new_account_carries_the_rule_and_nothing_else_does()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());
        var pro = new SessionAccount("claude", "a", "Claude Pro", "pro");
        var max = new SessionAccount("claude", "b", "Claude Max", "max");

        tile.Draw(ConversationReducer.Replay([
            new SessionConfigured(null, null, "token-pro") { Account = pro },
            new UserMessageAdded("m1", "first", []),
            new UserMessageAdded("m2", "still the same account", []),
            new SessionConfigured(null, null, "token-max") { Account = max },
            new UserMessageAdded("m3", "after the move", []),
        ]));

        Assert.Equal([null, null, "Claude Max"], tile.Timeline.Select(item => item.Seam));
    }

    [Fact]
    public void A_conversation_recorded_before_any_of_this_carries_no_rule()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());

        tile.Draw(ConversationReducer.Replay([
            new SessionConfigured("opus", null, "token"),
            new UserMessageAdded("m1", "one", []),
            new UserMessageAdded("m2", "two", []),
        ]));

        Assert.All(tile.Timeline, item => Assert.Null(item.Seam));
    }

    [Fact]
    public void A_switch_the_user_has_just_confirmed_is_not_adopted_back_to_the_account_it_left()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var elsewhere = Beside(settings, here, signIn: "second-subscription");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = elsewhere.Id });

        // What the replayed host still names is the stretch before the switch.
        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured("opus", "Plan", "token", "Max")
                {
                    Account = new SessionAccount("claude", here.Id, here.Name, here.SignInId),
                },
            ]),
            tile.Agent, accountWasJustPicked: true);

        Assert.Equal(elsewhere.Id, tile.Instance.Id);
        Assert.Null(tile.Overrides.Model);
        Assert.Equal(AiBehaviour.Plan, tile.Overrides.Behaviour);
        Assert.Equal(AiEffort.Max, tile.Overrides.Effort);
    }

    [Fact]
    public void A_model_spelled_for_another_account_is_left_behind_with_that_account()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });

        // A stretch that ran on an instance this machine no longer has: the account cannot be taken back, so
        // neither can the model id, which was resolved against that account's own provider.
        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured("some-providers-own-id", "Plan", "token")
                {
                    Account = new SessionAccount("claude", "a-row-since-deleted", "Gone", "other"),
                },
            ]),
            tile.Agent);

        Assert.Null(tile.Overrides.Model);
        Assert.Equal(AiBehaviour.Plan, tile.Overrides.Behaviour);
        // And the lost login is said before the first message, not only by a seam after it.
        Assert.Contains("\"Gone\"", tile.LaunchNotice);
    }

    [Fact]
    public void A_stored_login_that_is_the_one_running_needs_no_notice()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });

        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured(null, null, "token")
                {
                    Account = new SessionAccount("claude", "a-row-since-deleted", "Gone", SignInId: null),
                },
            ]),
            tile.Agent);

        Assert.False(tile.HasLaunchNotice);
    }

    [Fact]
    public void The_lost_login_notice_is_taken_down_by_the_next_start_that_does_not_need_it()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });

        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured(null, null, "token")
                {
                    Account = new SessionAccount("claude", "a-row-since-deleted", "Gone", "other"),
                },
            ]),
            tile.Agent);
        Assert.True(tile.HasLaunchNotice);

        // The conversation has since recorded the login the tile runs as, or another conversation was opened.
        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured(null, null, "token") { Account = new SessionAccount("claude", here.Id) },
            ]),
            tile.Agent);

        Assert.False(tile.HasLaunchNotice);
    }

    [Fact]
    public void The_row_the_tile_already_holds_is_not_taken_again_and_keeps_the_model_picked_in_the_strip()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });
        tile.AdoptStoredSession(ConversationReducer.Replay([new SessionModelChosen("sonnet")]), tile.Agent);

        // The same row, whose sign-in has been edited in Settings since that stretch ran.
        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured(null, null, "token")
                {
                    Account = new SessionAccount("claude", here.Id, here.Name, "a-sign-in-since-changed"),
                },
            ]),
            tile.Agent, settingsToo: false);

        Assert.Equal(here.Id, tile.Instance.Id);
        Assert.Equal("sonnet", tile.Overrides.Model);
        // The login moved under the same row, so the resume starts cold — said before the first message.
        Assert.True(tile.HasLaunchNotice);
    }

    [Fact]
    public void Opening_a_conversation_puts_the_tile_back_on_the_instance_it_last_ran_as()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var signIn = new AiSignIn { AgentId = "claude", Name = "Second subscription" };
        settings.Service.Settings.AiSignIns.Add(signIn);
        var stored = Beside(settings, here, signIn: signIn.Id);
        Assert.True(AiAgentCatalog.IsAvailable(stored, settings.Service.Settings));
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });

        tile.AdoptStoredSession(
            ConversationReducer.Replay([
                new SessionConfigured(null, null, "token")
                {
                    Account = new SessionAccount("claude", stored.Id, stored.Name, signIn.Id),
                },
            ]),
            tile.Agent);

        Assert.Equal(stored.Id, tile.Instance.Id);
        Assert.False(tile.HasLaunchNotice);
    }

    [Fact]
    public async Task A_picked_account_nothing_has_recorded_yet_survives_the_layout()
    {
        using var settings = new TempSettings();
        var here = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var elsewhere = Beside(settings, here, signIn: here.SignInId);
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = here.Id });

        await tile.SwitchInstanceAsync(elsewhere);
        var saved = ((ITileKind)new AgentConversationTileKind(TestTiles.ConversationStore())).Save(tile);

        Assert.Equal(true, saved?[AgentConversationTileKind.AccountPickedKey]?.GetValue<bool>());
        using var reopened = NewTile(settings, saved!);
        Assert.True(reopened.AccountPickedButUnrecorded);
        Assert.Equal(elsewhere.Id, reopened.Instance.Id);
    }

    [Fact]
    public void A_tile_nobody_has_switched_writes_no_picked_account()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject());

        var saved = ((ITileKind)new AgentConversationTileKind(TestTiles.ConversationStore())).Save(tile);

        Assert.False(saved!.ContainsKey(AgentConversationTileKind.AccountPickedKey));
    }

    /// <summary>A second configured instance of the same agent, on the login named.</summary>
    private static AiAgentInstance Beside(TempSettings settings, AiAgentInstance instance, string? signIn)
    {
        var copy = AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.Find(instance.AgentId)!);
        copy.Name = $"{instance.Name} elsewhere";
        copy.SignInId = signIn;
        settings.Service.Settings.AiAgentInstances.Add(copy);
        return copy;
    }

    private static AgentConversationTileViewModel NewTile(TempSettings settings, JsonObject state,
        mTiles.AgentSessions.Storage.IConversationStore? store = null, string tileId = "") =>
        (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                store ?? TestTiles.ConversationStore(), NoSessionStarter.Instance))
            .Create(new TileContext(Path.GetTempPath(), settings.Service) { TileId = () => tileId }, state);

    /// <summary>A tile on the instance given, opened on a conversation the CLI holds a session for.</summary>
    private static async Task<AgentConversationTileViewModel> StartedWithASession(TempSettings settings,
        AiAgentInstance instance, mTiles.AgentSessions.Storage.IConversationStore? store = null)
    {
        store ??= TestTiles.ConversationStore();
        var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id }, store,
            Guid.NewGuid().ToString());
        store.Save(new mTiles.AgentSessions.Storage.ConversationRecord(tile.ConversationId, instance.AgentId,
            Path.GetTempPath(), "session-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        tile.EnsureStarted();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tile.LaunchProblem is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(tile.LaunchProblem);
        return tile;
    }

    /// <summary>Prepares no launch, so a start stops with a problem before any process exists.</summary>
    private sealed class NoSessionStarter : IAgentSessionStarter
    {
        public static NoSessionStarter Instance { get; } = new();

        public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings, IAiAgent agent,
            AiAgentInstance instance, string workingDirectory, string conversationId, string? resumeToken,
            CancellationToken ct) =>
            Task.FromResult<(AgentSessionLaunch?, string?)>((null, "No agent is started in these tests."));

        public mTiles.AgentSessions.IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch,
            mTiles.AgentSessions.IAgentEventSink sink) =>
            throw new InvalidOperationException("No agent is started in these tests.");
    }
}
