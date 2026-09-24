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

    /// <summary>Asked through its own route, and never as a message: a slash command sent would stand in the
    /// transcript as something the user said.</summary>
    [Fact]
    public async Task A_session_with_a_route_is_asked_to_compact_and_nothing_is_sent_as_a_message()
    {
        var session = new CompactingFakeSession { ReportsOptions = true };
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await host.StartAsync(sink => session.Bind(sink), null, CancellationToken.None);

        await host.ExecuteAsync(new CompactContext(), CancellationToken.None);

        Assert.Equal(1, session.Compactions);
        Assert.Empty(session.Sent);
        Assert.Empty(host.State.Timeline);
    }

    [Fact]
    public async Task An_agent_with_no_route_says_so_rather_than_doing_nothing()
    {
        var session = new FakeAgentSession { ReportsOptions = true };
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await host.StartAsync(sink => session.Bind(sink), null, CancellationToken.None);

        await host.ExecuteAsync(new CompactContext(), CancellationToken.None);

        var notice = Assert.IsType<NoticeEntry>(Assert.Single(host.State.Timeline));
        Assert.Contains("cannot compact", notice.Text);
    }

    [Fact]
    public async Task Whether_the_control_is_offered_is_the_hosts_answer_and_not_the_sessions()
    {
        // The session reports its options the way every real one does — and says nothing about compacting,
        // because the answer is whether the object the host holds implements the interface.
        var compacting = new CompactingFakeSession { ReportsOptions = true };
        await using (var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null))
        {
            await host.StartAsync(sink => compacting.Bind(sink), null, CancellationToken.None);
            compacting.Say(new SessionOptionsReported([], [], []));
            Assert.True(host.State.Options!.CanCompact);
        }

        var plain = new FakeAgentSession { ReportsOptions = true };
        await using var second = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        await second.StartAsync(sink => plain.Bind(sink), null, CancellationToken.None);
        plain.Say(new SessionOptionsReported([], [], []));
        Assert.False(second.State.Options!.CanCompact);
    }

    [Fact]
    public void The_tile_offers_it_only_while_something_that_can_do_it_is_running()
    {
        using var settings = new TempSettings();
        var vm = ConversationTiles.New(settings);
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
        var instance = mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(
            mTiles.Services.Agents.AiAgentCatalog.Find("claude")!);
        // A window of its own, so no lookup of one runs.
        instance.MaxContextTokens = 200_000;
        var vm = ConversationTiles.New(settings, instance);
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
        var starter = new ReadyStarter { NewSession = () => new CompactingFakeSession { ReportsOptions = true } };
        var vm = ConversationTiles.New(settings, starter: starter);
        try
        {
            await ConversationTiles.StartUntilRunning(vm);
            Assert.True(vm.CanCompact);
            var session = (CompactingFakeSession)starter.Session!;

            // Said no: nothing reaches the agent.
            var asked = 0;
            vm.ConfirmExpectingYes = _ =>
            {
                asked++;
                return Task.FromResult(false);
            };
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(1, asked);
            Assert.Equal(0, session.Compactions);

            // Said yes.
            vm.ConfirmExpectingYes = _ =>
            {
                asked++;
                return Task.FromResult(true);
            };
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(2, asked);
            Assert.Equal(1, session.Compactions);

            // No window to ask in is a yes here, and deliberately not the "no" every question about
            // throwing something away answers: this takes nothing away.
            vm.ConfirmExpectingYes = null;
            await vm.CompactCommand.ExecuteAsync(null);
            Assert.Equal(2, session.Compactions);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
