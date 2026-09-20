using mTiles.AgentSessions;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Compacting a conversation's context — see <see cref="ICompactingSession"/>, whose per-agent routes were
/// measured rather than guessed.
/// </summary>
/// <remarks>What is pinned here is the half that is ours: that only a session with a route is asked, that
/// a session without one is told out loud rather than ignored, and that what decides whether the control
/// is drawn is the object the host is holding rather than anything a session says about itself.</remarks>
public class CompactContextTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mtiles-compact-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static ConversationRecord Record() =>
        new("tile-1", "claude", "/w", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_session_with_a_route_is_asked_to_compact()
    {
        var session = new CompactingSession();
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await host.StartAsync(sink => session.Bind(sink), null, CancellationToken.None);

        await host.ExecuteAsync(new CompactContext(), CancellationToken.None);

        Assert.Equal(1, session.Compactions);
    }

    [Fact]
    public async Task An_agent_with_no_route_says_so_rather_than_doing_nothing()
    {
        var session = new PlainSession();
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await host.StartAsync(sink => session.Bind(sink), null, CancellationToken.None);

        await host.ExecuteAsync(new CompactContext(), CancellationToken.None);

        var notice = Assert.IsType<NoticeEntry>(Assert.Single(host.State.Timeline));
        Assert.Contains("cannot compact", notice.Text);
    }

    [Fact]
    public async Task Nothing_is_sent_as_a_message_so_no_slash_command_lands_in_the_transcript()
    {
        var session = new CompactingSession();
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await host.StartAsync(sink => session.Bind(sink), null, CancellationToken.None);

        await host.ExecuteAsync(new CompactContext(), CancellationToken.None);

        Assert.Empty(session.Sent);
        Assert.Empty(host.State.Timeline);
    }

    [Fact]
    public async Task Whether_the_control_is_offered_is_the_hosts_answer_and_not_the_sessions()
    {
        // The session reports its options the way every real one does — and says nothing about compacting,
        // because the answer is whether the object the host holds implements the interface.
        var compacting = new CompactingSession();
        await using (var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null))
        {
            await host.StartAsync(sink => compacting.Bind(sink), null, CancellationToken.None);
            compacting.Say(new SessionOptionsReported([], [], []));
            Assert.True(host.State.Options!.CanCompact);
        }

        var plain = new PlainSession();
        await using var second = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await second.StartAsync(sink => plain.Bind(sink), null, CancellationToken.None);
        plain.Say(new SessionOptionsReported([], [], []));
        Assert.False(second.State.Options!.CanCompact);
    }

    [Fact]
    public void The_tile_offers_it_only_while_something_that_can_do_it_is_running()
    {
        using var settings = new TempSettings();
        var agent = mTiles.Services.Agents.AiAgentCatalog.Find("claude")!;
        var instance = mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(agent);
        // No account, so nothing is ever launched: what is under test is the drawing, not a CLI.
        instance.ApiAccountId = "no-such-account";
        var vm = new mTiles.ViewModels.AgentConversation.AgentConversationTileViewModel(
            Path.GetTempPath(), settings.Service, new SqliteConversationStore(_path), instance, agent,
            () => "tile", post: action => action());
        try
        {
            // A session that can compact, running.
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []) { CanCompact = true },
            ]));
            Assert.True(vm.CanCompact);

            // The same options after the agent has stopped: the options survive the session, and a control
            // offered over a dead agent can only answer that nothing is running.
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionOptionsReported([], [], []) { CanCompact = true },
                new SessionStateChanged(AgentSessionState.Stopped),
            ]));
            Assert.False(vm.CanCompact);

            // And an agent with no route never offers it at all.
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []),
            ]));
            Assert.False(vm.CanCompact);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void It_is_not_offered_during_a_turn_and_colours_itself_once_the_window_is_nearly_gone()
    {
        using var settings = new TempSettings();
        var agent = mTiles.Services.Agents.AiAgentCatalog.Find("claude")!;
        var instance = mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(agent);
        instance.ApiAccountId = "no-such-account";
        instance.MaxContextTokens = 200_000;
        var vm = new mTiles.ViewModels.AgentConversation.AgentConversationTileViewModel(
            Path.GetTempPath(), settings.Service, new SqliteConversationStore(_path), instance, agent,
            () => "tile", post: action => action());
        try
        {
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []) { CanCompact = true },
                new UsageUpdated(new TokenUsage(40_000, 200_000)),
            ]));
            Assert.True(vm.CompactCommand.CanExecute(null));
            Assert.False(vm.IsContextTight);

            // 80% is ModelContextWindow's margin, not a second opinion about when a window is full.
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []) { CanCompact = true },
                new UsageUpdated(new TokenUsage(160_000, 200_000)),
            ]));
            Assert.True(vm.IsContextTight);
            Assert.Contains("nearly full", vm.CompactTip);

            // Mid-turn it fades rather than going: both agents measured refuse to compact while a turn runs.
            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []) { CanCompact = true },
                new TurnStarted { TurnId = "t" },
            ]));
            Assert.True(vm.CanCompact);
            Assert.False(vm.CompactCommand.CanExecute(null));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task It_asks_first_and_an_unwired_question_is_a_yes()
    {
        using var settings = new TempSettings();
        var agent = mTiles.Services.Agents.AiAgentCatalog.Find("claude")!;
        var instance = mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(agent);
        var starter = new CompactingStarter();
        var vm = new mTiles.ViewModels.AgentConversation.AgentConversationTileViewModel(
            Path.GetTempPath(), settings.Service, new SqliteConversationStore(_path), instance, agent,
            () => "tile", post: action => action(), sessionStarter: starter);
        try
        {
            vm.EnsureStarted();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (vm.IsStarting && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.False(vm.IsStarting);
            Assert.True(vm.CanCompact);

            // Said no: nothing reaches the agent.
            var asked = 0;
            vm.ConfirmExpectingYes = _ =>
            {
                asked++;
                return Task.FromResult(false);
            };
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(1, asked);
            Assert.Equal(0, starter.Session!.Compactions);

            // Said yes.
            vm.ConfirmExpectingYes = _ =>
            {
                asked++;
                return Task.FromResult(true);
            };
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(2, asked);
            Assert.Equal(1, starter.Session.Compactions);

            // No window to ask in is a yes here, and deliberately not the "no" every question about
            // throwing something away answers: this takes nothing away.
            vm.ConfirmExpectingYes = null;
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(2, starter.Session.Compactions);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Starts nothing and hands back a session that can compact — what is under test is the
    /// question in front of the button, not a CLI the machine running this may not have.</summary>
    private sealed class CompactingStarter : mTiles.Services.Agents.Sessions.IAgentSessionStarter
    {
        public CompactingSession? Session { get; private set; }

        public Task<(mTiles.Services.Agents.Sessions.AgentSessionLaunch? Launch, string? Problem)> PrepareAsync(
            mTiles.Models.AppSettings settings, mTiles.Services.Agents.IAiAgent agent,
            mTiles.Models.AiAgentInstance instance, string workingDirectory, string conversationId,
            string? resumeToken, CancellationToken ct) =>
            Task.FromResult<(mTiles.Services.Agents.Sessions.AgentSessionLaunch?, string?)>(
                (new mTiles.Services.Agents.Sessions.AgentSessionLaunch(agent.BinaryName, workingDirectory,
                    mTiles.Services.Providers.AgentRuntime.For(settings, instance, agent: agent),
                    new Dictionary<string, string?>(), mTiles.Models.AiBehaviour.ToolDefault,
                    mTiles.Models.AiEffort.ToolDefault, resumeToken, conversationId), null));

        public IAgentSession Create(mTiles.Services.Agents.IAiAgent agent,
            mTiles.Services.Agents.Sessions.AgentSessionLaunch launch, IAgentEventSink sink)
        {
            var session = new CompactingSession();
            session.Bind(sink);
            // The options every real session reports at start; CanCompact on them is the host's stamp.
            Session = session;
            return session;
        }
    }

    /// <summary>A session with no route for it — pi, agy and Grok.</summary>
    private class PlainSession : IAgentSession
    {
        private IAgentEventSink? _sink;
        public List<string> Sent { get; } = [];

        public IAgentSession Bind(IAgentEventSink sink)
        {
            _sink = sink;
            return this;
        }

        public void Say(AgentEvent e) => _sink!.Emit(e);

        public Task StartAsync(CancellationToken ct)
        {
            _sink?.Emit(new SessionOptionsReported([], [], []));
            _sink?.Emit(new SessionStateChanged(AgentSessionState.Ready));
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

    /// <summary>Claude Code, codex and opencode.</summary>
    private sealed class CompactingSession : PlainSession, ICompactingSession
    {
        public int Compactions { get; private set; }

        public Task CompactAsync(CancellationToken ct)
        {
            Compactions++;
            return Task.CompletedTask;
        }
    }
}
