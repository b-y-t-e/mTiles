using mTiles.AgentSessions;
using System.Text.Json.Nodes;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Tiles;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Handing the work to another agent: what is written down, what is thrown away, and what is asked first.
/// </summary>
/// <remarks>
/// A conversation used to be locked to its agent from the first message, because no CLI can resume
/// another's session. That is still true and is no longer the whole story: the transcript is this
/// application's and the working tree is on disk, so the work can move even though the session cannot.
/// </remarks>
public class ConversationSeamTests
{
    /// <remarks>The one that costs a conversation if it is wrong. The token is the outgoing CLI's own
    /// handle: handed to the agent arriving, <c>codex resume</c> opens an interactive picker a launch waits
    /// on for ever, and <c>agy --conversation</c> warns, starts a fresh conversation and exits 0 — so the
    /// tile cannot tell a resumed session from a lost one.</remarks>
    [Fact]
    public void The_seam_moves_the_record_onto_the_new_agent_and_drops_the_old_token()
    {
        var store = TestTiles.ConversationStore();
        var record = Stored(store, "claude", "claude-session-id");

        var moved = HandoverWriter.Write(store, record, new SessionAccount("claude"),
            new SessionAccount("codex"), "# Handover\n\nThe brief.");

        Assert.Equal("codex", moved.AgentId);
        Assert.Null(moved.ResumeToken);

        var reread = store.Find(record.Id);
        Assert.Equal("codex", reread!.AgentId);
        Assert.Null(reread.ResumeToken);
    }

    [Fact]
    public void The_seam_is_an_event_and_the_transcript_before_it_is_untouched()
    {
        var store = TestTiles.ConversationStore();
        var record = Stored(store, "claude", "claude-session-id");
        store.Append(record.Id, [
            new UserMessageAdded("m1", "Make it sort pinned rows first.", []) { Sequence = 1 },
        ]);

        HandoverWriter.Write(store, record, new SessionAccount("claude", InstanceName: "Claude Pro"),
            new SessionAccount("codex", InstanceName: "Codex"), "# Handover\n\nThe brief.");

        var state = ConversationReducer.Replay(store.ReadEvents(record.Id));
        Assert.IsType<MessageEntry>(state.Timeline[0]);
        var seam = Assert.IsType<HandoverEntry>(state.Timeline[1]);
        Assert.Equal("Claude Pro", seam.From!.InstanceName);
        Assert.Equal("codex", seam.To.AgentId);
        Assert.Contains("The brief.", seam.Brief);
    }

    /// <remarks>The state has to forget what the record forgot, or a viewer goes on offering a token the
    /// launch will never use. The plan goes with it because it was the outgoing agent's own account of its
    /// work — kept, it would be drawn as the arriving agent's to-do list before that agent has said a word,
    /// and it is not lost: the brief carries it.</remarks>
    [Fact]
    public void Replaying_a_seam_forgets_the_token_the_chosen_model_and_the_old_agents_plan()
    {
        var state = ConversationReducer.Replay([
            new SessionConfigured("opus", "Auto", "claude-session-id") { Account = new SessionAccount("claude") },
            new SessionModelChosen("opus"),
            new PlanUpdated(null, [new PlanStep("Something claude was doing", PlanStepStatus.InProgress)]),
            new HandoverRecorded(new SessionAccount("claude"), new SessionAccount("codex"), "brief"),
        ]);

        Assert.Null(state.ResumeToken);
        Assert.Null(state.ChosenModel);
        Assert.Null(state.Plan);
    }

    /// <remarks>Stamped with the account that is leaving, like every entry before it: the handover is the
    /// last thing that happened in that stretch, so the seam the view draws lands above the first entry of
    /// the next one rather than on the handover itself.</remarks>
    [Fact]
    public void The_seam_belongs_to_the_stretch_that_is_ending()
    {
        var state = ConversationReducer.Replay([
            new SessionConfigured(null, null, "t") { Account = new SessionAccount("claude") },
            new HandoverRecorded(null, new SessionAccount("codex"), "brief"),
        ]);

        Assert.Equal("claude", Assert.IsType<HandoverEntry>(state.Timeline[0]).Account!.AgentId);
    }

    [Fact]
    public async Task Handing_the_work_to_another_agent_asks_first_and_writes_nothing_when_refused()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        var (tile, record) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        var codex = InstanceOf(settings, "codex");

        var asked = 0;
        tile.ConfirmAction = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };

        await tile.SwitchInstanceAsync(codex);

        Assert.Equal(1, asked);
        Assert.Equal("claude", tile.Agent.Id);
        Assert.Equal("claude", store.Find(record.Id)!.AgentId);
        Assert.DoesNotContain(store.ReadEvents(record.Id), e => e is HandoverRecorded);
    }

    [Fact]
    public async Task Taking_the_handover_moves_the_tile_and_records_what_the_new_agent_was_told()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        var (tile, record) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        var codex = InstanceOf(settings, "codex");
        tile.ConfirmAction = _ => Task.FromResult(true);

        await tile.SwitchInstanceAsync(codex);

        Assert.Equal("codex", tile.Agent.Id);
        Assert.Equal(codex.Id, tile.Instance.Id);

        var moved = store.Find(record.Id)!;
        Assert.Equal("codex", moved.AgentId);
        Assert.Null(moved.ResumeToken);

        var seam = Assert.IsType<HandoverRecorded>(
            store.ReadEvents(record.Id).Last(e => e is HandoverRecorded));
        Assert.Equal("codex", seam.To.AgentId);
        Assert.Contains("Make it sort pinned rows first.", seam.Brief);
    }

    /// <remarks>Carrying them is what the switch is for: dropped, somebody working in bypass came back on
    /// the tool's own asking without being told, which is a change of permissions nobody made. The arriving
    /// agent's own lists narrow them at launch (<c>AiProcessRunner.Fit</c>), so a mode it does not have is
    /// rounded down there rather than refused here.</remarks>
    [Fact]
    public async Task The_permission_mode_and_the_effort_travel_with_the_work()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        var (tile, _) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        tile.ConfirmAction = _ => Task.FromResult(true);
        tile.KeepOverride(new SessionSettings(Mode: "BypassPermissions", Effort: "Max"));

        await tile.SwitchInstanceAsync(InstanceOf(settings, "codex"));

        Assert.Equal(AiBehaviour.BypassPermissions, tile.Overrides.Behaviour);
        Assert.Equal(AiEffort.Max, tile.Overrides.Effort);
        // Spelled for the provider behind the account that is leaving, so it never travels.
        Assert.Null(tile.Overrides.Model);
    }

    /// <remarks><b>An override is what the tile runs differently from its instance, so a tile nobody has
    /// touched the picker in has none at all.</b> Carried as overrides, that tile handed the arriving agent
    /// nothing and it started on its own instance's defaults instead — which on a row configured for bypass
    /// is a CLI editing without asking, under a dialog that said nothing about it. What travels is what the
    /// tile was actually running.</remarks>
    [Fact]
    public async Task The_mode_that_travels_is_the_one_the_tile_was_running_not_the_one_it_overrode()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        InstanceOf(settings, "claude").DefaultBehaviour = AiBehaviour.BypassPermissions;
        InstanceOf(settings, "claude").DefaultEffort = AiEffort.Max;
        var (tile, _) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        string? asked = null;
        tile.ConfirmAction = message =>
        {
            asked = message;
            return Task.FromResult(true);
        };

        // Nothing picked in the strip: the mode is the outgoing instance's own answer.
        Assert.Null(tile.Overrides.Behaviour);
        await tile.SwitchInstanceAsync(InstanceOf(settings, "codex"));

        Assert.Equal(AiBehaviour.BypassPermissions, tile.Overrides.Behaviour);
        Assert.Equal(AiEffort.Max, tile.Overrides.Effort);
        Assert.Contains("bypass", asked ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>Everything on this path runs under the tile's own catch-and-log, so a store that will not
    /// write left the picker on the agent arriving, the tile on the agent leaving and the user with nothing
    /// but a line in the log. And the two writes are one move: a row naming the new agent over a transcript
    /// that never says it moved is refused by the next start with no account of where it came from.</remarks>
    [Fact]
    public async Task A_seam_that_cannot_be_written_leaves_the_conversation_where_it_was_and_says_so()
    {
        using var settings = new TempSettings();
        var store = new RefusesToAppend(TestTiles.ConversationStore());
        var (tile, record) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        tile.ConfirmAction = _ => Task.FromResult(true);

        store.Refusing = true;
        await tile.SwitchInstanceAsync(InstanceOf(settings, "codex"));

        Assert.Equal("claude", tile.Agent.Id);
        Assert.Equal("claude", store.Find(record.Id)!.AgentId);
        Assert.Equal("claude-session-id", store.Find(record.Id)!.ResumeToken);
        Assert.Contains(AgentConversationTileViewModel.HandoverNotWrittenNotice, tile.LaunchNotice);
    }

    /// <remarks>The sentence says the work "stays with the agent it was already running", which stops being
    /// true the moment a retry works. Left standing, it would go on saying so over a conversation that has
    /// just moved, until somebody dismissed it by hand.</remarks>
    [Fact]
    public async Task The_sentence_about_a_failed_handover_comes_down_when_one_works()
    {
        using var settings = new TempSettings();
        var store = new RefusesToAppend(TestTiles.ConversationStore());
        var (tile, _) = await OnAConversationWithSomethingSaid(settings, store);
        using var owned = tile;
        tile.ConfirmAction = _ => Task.FromResult(true);

        store.Refusing = true;
        await tile.SwitchInstanceAsync(InstanceOf(settings, "codex"));
        Assert.Contains(AgentConversationTileViewModel.HandoverNotWrittenNotice, tile.LaunchNotice);

        store.Refusing = false;
        await tile.SwitchInstanceAsync(InstanceOf(settings, "codex"));

        Assert.Equal("codex", tile.Agent.Id);
        Assert.DoesNotContain(AgentConversationTileViewModel.HandoverNotWrittenNotice, tile.LaunchNotice);
    }

    /// <summary>A store whose append fails, which is the half of the seam that cannot be retried in place.
    /// </summary>
    private sealed class RefusesToAppend(IConversationStore inner) : IConversationStore
    {
        public bool Refusing { get; set; }

        public ConversationRecord? Find(string conversationId) => inner.Find(conversationId);

        public IReadOnlyList<ConversationSummary> List(string workingDirectory) => inner.List(workingDirectory);

        public void Save(ConversationRecord record) => inner.Save(record);

        public IReadOnlyList<AgentEvent> ReadEvents(string conversationId) => inner.ReadEvents(conversationId);

        public long LastSequence(string conversationId) => inner.LastSequence(conversationId);

        public void Append(string conversationId, IReadOnlyList<AgentEvent> events)
        {
            if (Refusing) throw new IOException("The conversation store is busy.");
            inner.Append(conversationId, events);
        }

        public void Delete(string conversationId) => inner.Delete(conversationId);
    }

    /// <remarks>
    /// <para><b>The half the seam is written for.</b> A conversation whose transcript says the work moved,
    /// opened on an agent that was never told anything about it, is worse than a refusal — so the brief is
    /// delivered by the first start that reaches a live session.</para>
    /// <para><b>And it is not something the user said.</b> It goes out as a turn like any other and leaves
    /// no message of theirs behind: the timeline already carries it, folded, on the handover entry.</para>
    /// </remarks>
    [Fact]
    public async Task The_first_session_that_starts_after_a_seam_is_told_what_the_work_is()
    {
        using var settings = new TempSettings();
        var store = TestTiles.ConversationStore();
        var starter = new LiveSession();

        using var tile = await OnAHandedOverConversation(settings, store, starter);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (starter.Session!.Sent.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        var told = Assert.Single(starter.Session!.Sent);
        Assert.Contains("Make it sort pinned rows first.", told);
        Assert.Single(store.ReadEvents(tile.ConversationId).OfType<UserMessageAdded>());
    }

    /// <summary>A conversation already handed from Claude Code to codex, opened in a codex tile.</summary>
    private static async Task<AgentConversationTileViewModel> OnAHandedOverConversation(TempSettings settings,
        IConversationStore store, IAgentSessionStarter starter)
    {
        var codex = InstanceOf(settings, "codex");
        var tileId = Guid.NewGuid().ToString();
        var record = new ConversationRecord(tileId, "claude", Path.GetTempPath(), "claude-session-id",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.Save(record);
        store.Append(record.Id, [
            new UserMessageAdded("m1", "Make it sort pinned rows first.", []) { Sequence = 1 },
            new AssistantMessageCompleted("a1", "Done.") { Sequence = 2 },
        ]);
        HandoverWriter.Write(store, record, new SessionAccount("claude"), new SessionAccount("codex", codex.Id),
            ConversationHandover.Write(ConversationReducer.Replay(store.ReadEvents(record.Id))));

        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(store, starter))
            .Create(
                new TileContext(Path.GetTempPath(), settings.Service) { TileId = () => tileId },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = codex.Id });

        tile.EnsureStarted();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (tile.IsStarting && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Null(tile.LaunchProblem);
        return tile;
    }

    /// <summary>Starts no CLI and hands back a session that records what it was sent.</summary>
    private sealed class LiveSession : IAgentSessionStarter
    {
        public RecordingSession? Session { get; private set; }

        public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings,
            IAiAgent agent, AiAgentInstance instance, string workingDirectory, string conversationId,
            string? resumeToken, CancellationToken ct) =>
            Task.FromResult<(AgentSessionLaunch?, string?)>((new AgentSessionLaunch(agent.BinaryName,
                workingDirectory, mTiles.Services.Providers.AgentRuntime.For(settings, instance, agent: agent),
                new Dictionary<string, string?>(), AiBehaviour.ToolDefault, AiEffort.ToolDefault, resumeToken,
                conversationId), null));

        public mTiles.AgentSessions.IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch,
            mTiles.AgentSessions.IAgentEventSink sink)
        {
            var session = new RecordingSession(sink);
            Session = session;
            return session;
        }
    }

    private sealed class RecordingSession(mTiles.AgentSessions.IAgentEventSink sink) : mTiles.AgentSessions.IAgentSession
    {
        public List<string> Sent { get; } = [];

        public Task StartAsync(CancellationToken ct)
        {
            sink.Emit(new SessionStateChanged(AgentSessionState.Ready));
            return Task.CompletedTask;
        }

        public Task SendAsync(AgentTurnInput input, CancellationToken ct)
        {
            Sent.Add(input.Text);
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AnswerQuestionsAsync(string requestId,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? answers, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct) =>
            Task.FromResult(SettingsChangeOutcome.Applied);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ConversationRecord Stored(IConversationStore store, string agentId, string? token)
    {
        var record = new ConversationRecord(Guid.NewGuid().ToString(), agentId, Path.GetTempPath(), token,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.Save(record);
        return record;
    }

    private static AiAgentInstance InstanceOf(TempSettings settings, string agentId)
    {
        var instance = settings.Service.Settings.AiAgentInstances.FirstOrDefault(i => i.AgentId == agentId);
        if (instance is not null) return instance;

        instance = AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.Find(agentId)!);
        settings.Service.Settings.AiAgentInstances.Add(instance);
        return instance;
    }

    /// <summary>A tile whose conversation already holds a message said to Claude Code.</summary>
    /// <remarks>Started, so the tile holds a host that has replayed the conversation — which is what the
    /// brief is folded out of — and stopped at a launch problem, because no agent is run in these tests.
    /// </remarks>
    private static async Task<(AgentConversationTileViewModel Tile, ConversationRecord Record)>
        OnAConversationWithSomethingSaid(TempSettings settings, IConversationStore store)
    {
        var claude = InstanceOf(settings, "claude");
        // One id, asked for many times: a tile's conversation is named after it, and a TileId that answered
        // differently on each call would give the start and the store two different conversations.
        var tileId = Guid.NewGuid().ToString();
        var tile = (AgentConversationTileViewModel)((ITileKind)new AgentConversationTileKind(
                store, NoLaunch.Instance))
            .Create(
                new TileContext(Path.GetTempPath(), settings.Service) { TileId = () => tileId },
                new JsonObject { [AgentStateKeys.InstanceIdKey] = claude.Id });

        var record = new ConversationRecord(tile.ConversationId, "claude", Path.GetTempPath(), "claude-session-id",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.Save(record);
        store.Append(record.Id, [
            new UserMessageAdded("m1", "Make it sort pinned rows first.", []) { Sequence = 1 },
            new AssistantMessageCompleted("a1", "Done.") { Sequence = 2 },
        ]);

        tile.EnsureStarted();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tile.LaunchProblem is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(tile.LaunchProblem);
        return (tile, record);
    }

    private sealed class NoLaunch : IAgentSessionStarter
    {
        public static NoLaunch Instance { get; } = new();

        public Task<(AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(AppSettings settings,
            IAiAgent agent, AiAgentInstance instance, string workingDirectory, string conversationId,
            string? resumeToken, CancellationToken ct) =>
            Task.FromResult<(AgentSessionLaunch?, string?)>((null, "No agent is started in these tests."));

        public mTiles.AgentSessions.IAgentSession Create(IAiAgent agent, AgentSessionLaunch launch,
            mTiles.AgentSessions.IAgentEventSink sink) =>
            throw new InvalidOperationException("No agent is started in these tests.");
    }
}
