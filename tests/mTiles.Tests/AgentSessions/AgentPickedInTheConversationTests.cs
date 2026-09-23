using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.AgentSessions;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Tiles;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The Agent tile asks nothing before it opens: the agent is picked in the conversation, and is settled once
/// the conversation has something in it.
/// </summary>
/// <remarks>It used to ask first, the way a terminal tile asks for a shell — the wrong question for a
/// conversation, which is bound to nobody until something is said in it. What replaced the step is the
/// chooser in the strip. Another agent on a started conversation is a <i>handover</i>: the resume token and
/// the stored events are the holding agent's and no other CLI can continue that session, but the work can
/// move, so the pick asks rather than being refused.</remarks>
public class AgentPickedInTheConversationTests
{
    [Fact]
    public void Creating_the_tile_asks_nothing()
    {
        using var settings = new TempSettings();
        var kind = Kind(settings);

        Assert.Empty(kind.SetupOptions(new TileContext(Path.GetTempPath(), settings.Service)));
    }

    [Fact]
    public void A_new_tile_opens_on_the_agent_the_last_one_was_pointed_at()
    {
        using var settings = new TempSettings();
        var second = settings.Service.Settings.AiAgentInstances
            .First(i => AiAgentCatalog.Find(i.AgentId) is mTiles.Services.Agents.Sessions.IConversationalAgent
                        && i.Id != Conversational(settings).First().Id);
        settings.Service.Settings.LastAgentInstanceId = second.Id;

        using var tile = NewTile(settings, new JsonObject());

        Assert.Equal(second.Id, tile.Instance.Id);
    }

    /// <remarks>Nothing said yet: the conversation belongs to nobody, so another agent is simply taken and
    /// no question is worth asking. Once something has been said, the same pick hands the work over — and a
    /// tile with no dialog to ask in takes that as a no, unlike a change of account: a handover starts
    /// another CLI on somebody's repository with a brief they have not read.</remarks>
    [Fact]
    public async Task Another_agent_is_taken_while_nothing_has_been_said_and_asked_about_afterwards()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var codex = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "codex");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        await tile.SwitchInstanceAsync(codex);
        Assert.Equal(codex.Id, tile.Instance.Id);
        Assert.Equal("codex", tile.Agent.Id);
        // What a new tile opens on, and what this tile's layout says.
        Assert.Equal(codex.Id, settings.Service.Settings.LastAgentInstanceId);

        tile.Draw(mTiles.AgentSessions.Conversation.ConversationReducer.Replay(
            [new mTiles.AgentSessions.Events.UserMessageAdded("u", "Hello", [])]));

        await tile.SwitchInstanceAsync(claude);
        Assert.Equal(codex.Id, tile.Instance.Id);
        Assert.True(tile.IsBoundToItsAgent);

        string? asked = null;
        tile.ConfirmAction = message =>
        {
            asked = message;
            return Task.FromResult(true);
        };
        await tile.SwitchInstanceAsync(claude);

        Assert.Contains("brief", asked ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(claude.Id, tile.Instance.Id);
    }

    [Fact]
    public async Task A_notice_that_the_start_failed_binds_nobody()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var codex = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "codex");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = codex.Id });

        tile.Draw(mTiles.AgentSessions.Conversation.ConversationReducer.Replay(
            [new mTiles.AgentSessions.Events.NoticeRaised(mTiles.AgentSessions.Events.NoticeLevel.Error, "Not logged in")]));
        Assert.NotEmpty(tile.Timeline);
        Assert.False(tile.IsBoundToItsAgent);

        await tile.SwitchInstanceAsync(claude);
        Assert.Equal(claude.Id, tile.Instance.Id);
    }

    [Fact]
    public async Task A_stored_conversation_binds_the_tile_to_its_own_agent_before_anything_is_drawn()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var codex = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "codex");
        var store = new mTiles.AgentSessions.Storage.SqliteConversationStore(
            Path.Combine(Path.GetTempPath(), $"mtiles-test-conversations-{Guid.NewGuid():N}.db"));
        store.Save(new mTiles.AgentSessions.Storage.ConversationRecord("stored", "claude", Path.GetTempPath(), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        store.Append("stored", [new mTiles.AgentSessions.Events.UserMessageAdded("u", "Hello", []) { Sequence = 1 }]);
        using var tile = StoredTile(settings, store, claude);

        // Picked while the tile is still empty: the store says Claude's conversation has been spoken in.
        await tile.SwitchInstanceAsync(codex);

        Assert.True(tile.IsBoundToItsAgent);
        Assert.Equal(claude.Id, tile.Instance.Id);
        Assert.NotEqual(codex.Id, settings.Service.Settings.LastAgentInstanceId);
        // The row that would hand the work over says so; the agent that holds it says nothing of the kind.
        Assert.DoesNotContain("hands the work over",
            tile.Chooser.Options.Single(o => o.Instance.Id == claude.Id).Detail);
        Assert.Contains("hands the work over from Claude",
            tile.Chooser.Options.Single(o => o.Instance.Id == codex.Id).Detail);
    }

    [Fact]
    public async Task A_stored_record_with_nothing_said_in_it_binds_nobody()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var codex = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "codex");
        var store = new mTiles.AgentSessions.Storage.SqliteConversationStore(
            Path.Combine(Path.GetTempPath(), $"mtiles-test-conversations-{Guid.NewGuid():N}.db"));
        // What a host writes as its session starts, before anything is said.
        store.Save(new mTiles.AgentSessions.Storage.ConversationRecord("stored", "claude", Path.GetTempPath(), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var tile = StoredTile(settings, store, claude);

        await tile.SwitchInstanceAsync(codex);

        Assert.Equal(codex.Id, tile.Instance.Id);
        Assert.False(tile.IsBoundToItsAgent);
        Assert.DoesNotContain("holds a conversation", tile.LaunchProblem ?? "");
    }

    [Fact]
    public async Task Another_instance_of_the_same_agent_is_refused_over_another_agents_stored_conversation()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var otherClaude = new AiAgentInstance { AgentId = "claude", Name = "Claude, second account" };
        settings.Service.Settings.AiAgentInstances.Add(otherClaude);
        var store = new mTiles.AgentSessions.Storage.SqliteConversationStore(
            Path.Combine(Path.GetTempPath(), $"mtiles-test-conversations-{Guid.NewGuid():N}.db"));
        store.Save(new mTiles.AgentSessions.Storage.ConversationRecord("stored", "codex", Path.GetTempPath(), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        store.Append("stored", [new mTiles.AgentSessions.Events.UserMessageAdded("u", "Hello", []) { Sequence = 1 }]);
        using var tile = StoredTile(settings, store, claude);

        // The same agent, but the store has not been read yet: what is stored is Codex's.
        await tile.SwitchInstanceAsync(otherClaude);

        Assert.Equal(claude.Id, tile.Instance.Id);
        Assert.NotEqual(otherClaude.Id, settings.Service.Settings.LastAgentInstanceId);
    }

    [Fact]
    public async Task A_switch_during_a_turn_asks_first_and_a_refusal_keeps_the_running_instance()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var otherClaude = new AiAgentInstance { AgentId = "claude", Name = "Claude, second account" };
        settings.Service.Settings.AiAgentInstances.Add(otherClaude);
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });
        string? asked = null;
        tile.ConfirmAction = message =>
        {
            asked = message;
            return Task.FromResult(false);
        };
        tile.IsWorking = true;

        await tile.SwitchInstanceAsync(otherClaude);

        Assert.NotNull(asked);
        Assert.Equal(claude.Id, tile.Instance.Id);
    }

    [Fact]
    public async Task Another_instance_of_the_same_agent_drops_the_model_and_keeps_mode_and_effort()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var otherClaude = new AiAgentInstance { AgentId = "claude", Name = "Claude, second account" };
        settings.Service.Settings.AiAgentInstances.Add(otherClaude);
        // A model picked on an OpenRouter account, which a subscription does not serve.
        using var tile = NewTile(settings, new JsonObject
        {
            [AgentStateKeys.InstanceIdKey] = claude.Id,
            [AgentConversationTileKind.ModelKey] = "z-ai/glm-5.3-flash",
            [AgentConversationTileKind.ModeKey] = SessionSettingOptions.ModeId(AiBehaviour.Plan),
            [AgentConversationTileKind.EffortKey] = SessionSettingOptions.EffortId(AiEffort.High),
        });

        await tile.SwitchInstanceAsync(otherClaude);

        Assert.Equal(otherClaude.Id, tile.Instance.Id);
        Assert.Equal(new SessionOverrides(null, AiBehaviour.Plan, AiEffort.High), tile.Overrides);
    }

    [Fact]
    public async Task Overlapping_picks_end_on_the_last_one_picked()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var codex = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "codex");
        var pi = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "pi");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        await Task.WhenAll(tile.SwitchInstanceAsync(codex), tile.SwitchInstanceAsync(pi));

        Assert.Equal(pi.Id, tile.Instance.Id);
        Assert.Equal(pi.Id, settings.Service.Settings.LastAgentInstanceId);
    }

    [Fact]
    public async Task A_substitute_that_runs_nothing_is_not_shown_as_running_and_can_be_accepted()
    {
        using var settings = new TempSettings();
        using var tile = NewTile(settings, new JsonObject
        {
            [AgentStateKeys.InstanceIdKey] = "deleted-instance",
            [AgentStateKeys.AgentIdKey] = "an-agent-this-build-does-not-know",
        });
        var substitute = tile.Instance;
        Assert.NotNull(tile.Substitution);
        Assert.Null(tile.Chooser.Selected);

        await tile.SwitchInstanceAsync(substitute);

        Assert.Null(tile.Substitution);
        Assert.Equal(substitute.Id, tile.Chooser.Selected?.Instance.Id);
    }

    [Fact]
    public async Task An_entry_refused_when_drawn_is_asked_again_when_picked()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        var later = new AiAgentInstance { Id = "later", Name = "Later", AgentId = "claude", SignInId = "added-later" };
        settings.Service.Settings.AiAgentInstances.Add(later);
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });
        var refused = tile.Chooser.Options.Single(o => o.Instance.Id == later.Id);
        Assert.False(refused.IsPickable);

        // Availability moved without Settings saying so, the way an install finishing does.
        settings.Service.Settings.AiSignIns.Add(new AiSignIn { Id = "added-later", AgentId = "claude", Name = "Later" });
        tile.Chooser.Selected = refused;

        // Waited on the redraw rather than on the instance, because the switch reads the store — which is
        // an await — and so finishes on a thread pool thread: the instance is taken a line before the list
        // is rebuilt, and asserting on the list the moment the instance moves reads the list from before it.
        await WaitUntil(() => tile.Chooser.Options.Single(o => o.Instance.Id == later.Id).IsPickable);
        Assert.Equal(later.Id, tile.Instance.Id);
    }

    private static Task WaitUntil(Func<bool> condition) => ConversationTiles.WaitUntil(condition, "the chooser");

    private static AgentConversationTileViewModel StoredTile(TempSettings settings,
        mTiles.AgentSessions.Storage.IConversationStore store, AiAgentInstance instance) =>
        ConversationTiles.FromKind(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = instance.Id },
            store, tileId: "stored");

    [Fact]
    public void Drawing_the_conversation_rebuilds_the_chooser_only_when_the_binding_moves()
    {
        using var settings = new TempSettings();
        var claude = settings.Service.Settings.AiAgentInstances.First(i => i.AgentId == "claude");
        using var tile = NewTile(settings, new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        var before = tile.Chooser.Options.ToList();
        tile.Draw(mTiles.AgentSessions.Conversation.ConversationReducer.Replay([]));
        // A draw per frame must not reset the list: an open chooser would flicker and every pass would ask
        // for an availability answer whose cache expires into a PATH scan on the UI thread.
        Assert.Equal(before, tile.Chooser.Options, ReferenceEqualityComparer.Instance);

        tile.Draw(mTiles.AgentSessions.Conversation.ConversationReducer.Replay(
            [new mTiles.AgentSessions.Events.UserMessageAdded("u", "Hello", [])]));
        Assert.NotEqual(before, tile.Chooser.Options, ReferenceEqualityComparer.Instance);
    }

    private static ITileKind Kind(TempSettings settings) =>
        TestTiles.Catalog(settings.Service).Entries
            .Single(e => e.Kind.Id == TileKindIds.AgentConversation).Kind;

    private static IEnumerable<AiAgentInstance> Conversational(TempSettings settings) =>
        settings.Service.Settings.AiAgentInstances
            .Where(i => AiAgentCatalog.Find(i.AgentId) is mTiles.Services.Agents.Sessions.IConversationalAgent);

    private static AgentConversationTileViewModel NewTile(TempSettings settings, JsonObject state) =>
        ConversationTiles.FromKind(settings, state);
}
