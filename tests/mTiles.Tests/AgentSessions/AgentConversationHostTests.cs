using mTiles.AgentSessions;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>The rules every viewer relies on — see <see cref="AgentConversationHost"/>.</summary>
public class AgentConversationHostTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mtiles-host-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static ConversationRecord Record(string agent = "claude") =>
        new("tile-1", agent, "/w", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_change_the_session_takes_is_what_the_conversation_now_runs_as()
    {
        var session = new FakeSession();
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        var restarts = 0;
        host.RestartRequested += _ => restarts++;
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings("opus", "Plan")), CancellationToken.None);

        Assert.Equal(("opus", "Plan"), (host.State.Model, host.State.Mode));
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task A_change_the_session_cannot_take_asks_for_a_restart_but_never_under_a_working_agent()
    {
        var session = new FakeSession { SettingsOutcome = SettingsChangeOutcome.NeedsRestart };
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        List<SessionSettings> restarts = [];
        host.RestartRequested += restarts.Add;
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        session.Say(new TurnStarted { TurnId = "t" });
        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings(Effort: "Max")), CancellationToken.None);
        Assert.Empty(restarts);
        Assert.Contains(host.State.Timeline, e => e is NoticeEntry { Level: NoticeLevel.Warning });

        session.Say(new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" });
        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings(Effort: "Max")), CancellationToken.None);
        Assert.Equal("Max", Assert.Single(restarts).Effort);
    }

    [Fact]
    public async Task A_change_the_agent_refuses_is_neither_kept_nor_restarted_for()
    {
        var session = new FakeSession { SettingsOutcome = SettingsChangeOutcome.Rejected };
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        var reported = 0;
        host.SettingsApplied += _ => reported++;
        host.RestartRequested += _ => reported++;
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings("opsu")), CancellationToken.None);

        Assert.Equal(0, reported);
        Assert.NotEqual("opsu", host.State.Model);
    }

    [Fact]
    public async Task A_change_taken_in_part_keeps_the_part_taken_and_restarts_only_for_the_rest()
    {
        var session = new FakeSession
        {
            OutcomeFor = change => change switch
            {
                { Model: not null } => SettingsChangeOutcome.Applied,
                { Mode: not null } => SettingsChangeOutcome.Rejected,
                _ => SettingsChangeOutcome.NeedsRestart,
            },
        };
        await using var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);
        List<SessionSettings> applied = [];
        List<SessionSettings> restarts = [];
        host.SettingsApplied += applied.Add;
        host.RestartRequested += restarts.Add;
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new ChangeSessionSettings(new SessionSettings("opus", "Plan", "Max")),
            CancellationToken.None);

        Assert.Equal(new SessionSettings(Model: "opus"), Assert.Single(applied));
        Assert.Equal(new SessionSettings(Effort: "Max"), Assert.Single(restarts));
        Assert.Equal("opus", host.State.Model);
        Assert.NotEqual("Plan", host.State.Mode);
    }

    [Fact]
    public async Task The_catalogue_a_session_reports_reaches_the_viewer_and_never_the_store()
    {
        var store = new SqliteConversationStore(_path);
        var session = new FakeSession();
        var host = new AgentConversationHost(Record(), store, null);
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        session.Say(new SessionOptionsReported([new SessionOption("opus", "Opus")], [], []));
        session.Say(new AssistantMessageCompleted("m", "done"));
        await host.DisposeAsync();

        Assert.Equal("opus", Assert.Single(host.State.Options!.Models).Id);
        Assert.DoesNotContain(store.ReadEvents("tile-1"), e => e is SessionOptionsReported);
        Assert.Contains(store.ReadEvents("tile-1"), e => e is AssistantMessageCompleted);
    }

    [Fact]
    public async Task A_message_is_recorded_by_the_host_and_a_turn_is_bracketed_by_two_checkpoints()
    {
        var store = new SqliteConversationStore(_path);
        var checkpoints = new FakeCheckpoints();
        var session = new FakeSession();
        var host = new AgentConversationHost(Record(), store, checkpoints);
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new SendMessage("fix the build"), CancellationToken.None);
        session.Say(new TurnStarted { TurnId = "t" });
        session.Say(new SessionConfigured("model", null, "resume-me"));
        session.Say(new AssistantMessageCompleted("m", "done"));
        session.Say(new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" });
        await checkpoints.Settled();
        await host.DisposeAsync();

        Assert.Equal(["fix the build"], session.Sent);
        Assert.Equal(2, checkpoints.Captured);
        var state = host.State;
        Assert.Equal("fix the build", Assert.IsType<MessageEntry>(state.Timeline[0]).Text);
        var checkpoint = Assert.IsType<CheckpointEntry>(state.Timeline[^1]);
        Assert.Equal(("cp-0", "cp-1"), (checkpoint.BaseCheckpointId, checkpoint.Id));
        Assert.Equal("resume-me", store.Find("tile-1")!.ResumeToken);
    }

    [Fact]
    public async Task A_reopened_conversation_is_drawn_from_the_store_and_hands_back_its_token()
    {
        var store = new SqliteConversationStore(_path);
        var first = new AgentConversationHost(Record(), store, null);
        var session = new FakeSession();
        await first.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await first.ExecuteAsync(new SendMessage("hello"), CancellationToken.None);
        session.Say(new SessionConfigured(null, null, "token-1"));
        session.Say(new ToolStarted("x", ToolKind.Command, "Bash", "ls", ToolDetail.Empty));
        await first.DisposeAsync();

        var reopened = new AgentConversationHost(Record(), store, null);

        Assert.Equal("token-1", reopened.ResumeToken);
        Assert.Equal("hello", Assert.IsType<MessageEntry>(reopened.State.Timeline[0]).Text);
        // The process that was running that tool is gone; the replay does not pretend otherwise.
        Assert.Equal(ToolCallState.Abandoned,
            Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(reopened.State.Timeline[1]).Items[0]).State);
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task Events_a_newer_build_wrote_are_not_overwritten_by_the_next_message()
    {
        var store = new SqliteConversationStore(_path);
        store.Save(Record());
        store.Append("tile-1", [new NoticeRaised(NoticeLevel.Info, "old") { Sequence = 1 }]);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO events (conversation_id, sequence, type, at, payload) VALUES ('tile-1', 2, 'Future', '', '{\"type\":\"from.the.future\"}')";
            command.ExecuteNonQuery();
        }

        var host = new AgentConversationHost(Record(), store, null);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await host.ExecuteAsync(new SendMessage("after rollback"), CancellationToken.None);
        await host.DisposeAsync();

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type FROM events WHERE conversation_id = 'tile-1' AND sequence = 2";
            Assert.Equal("Future", command.ExecuteScalar());
        }
        var reopened = new AgentConversationHost(Record(), store, null);
        Assert.Contains(reopened.State.Timeline, entry => entry is MessageEntry { Text: "after rollback" });
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task A_conversation_held_with_another_agent_is_not_handed_to_this_one()
    {
        var store = new SqliteConversationStore(_path);
        var codex = new AgentConversationHost(Record("codex"), store, null);
        var session = new FakeSession();
        await codex.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await codex.ExecuteAsync(new SendMessage("hi"), CancellationToken.None);
        session.Say(new SessionConfigured(null, null, "codex-thread"));
        await codex.DisposeAsync();

        var refused = Assert.Throws<ConversationOfAnotherAgentException>(
            () => new AgentConversationHost(Record("claude"), store, null));

        Assert.Equal("codex", refused.StoredAgentId);
        // Refusing is not forgetting: the conversation is still there for codex to come back to.
        Assert.Equal("codex-thread", store.Find("tile-1")!.ResumeToken);
    }

    [Fact]
    public async Task A_forgotten_conversation_leaves_nothing_behind_for_the_next_one()
    {
        var store = new SqliteConversationStore(_path);
        var host = new AgentConversationHost(Record(), store, null);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await host.ExecuteAsync(new SendMessage("old"), CancellationToken.None);
        session.Say(new AssistantTextDelta("m", "still queued"));

        await host.ForgetAsync(CancellationToken.None);
        await host.DisposeAsync();

        Assert.Empty(store.ReadEvents("tile-1"));
        var next = new AgentConversationHost(Record(), store, null);
        Assert.Empty(next.State.Timeline);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task A_turn_started_from_a_queued_message_is_measured_from_the_previous_turns_end()
    {
        var checkpoints = new FakeCheckpoints();
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new SendMessage("one"), CancellationToken.None);
        session.Say(new TurnStarted { TurnId = "t1" });
        await host.ExecuteAsync(new SendMessage("two"), CancellationToken.None);
        session.Say(new TurnCompleted(TurnOutcome.Completed) { TurnId = "t1" });
        session.Say(new TurnStarted { TurnId = "t2" });
        session.Say(new TurnCompleted(TurnOutcome.Completed) { TurnId = "t2" });
        await host.DisposeAsync();

        var turns = host.State.Timeline.OfType<CheckpointEntry>()
            .Select(c => (c.BaseCheckpointId, c.Id)).ToList();
        Assert.Equal([("cp-0", "cp-1"), ("cp-1", "cp-2")], turns);
    }

    [Fact]
    public async Task A_second_message_during_the_baseline_photograph_does_not_replace_it()
    {
        var capture = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new FakeCheckpoints { FirstCapture = capture.Task };
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        var first = host.ExecuteAsync(new SendMessage("one"), CancellationToken.None);
        var second = host.ExecuteAsync(new SendMessage("two"), CancellationToken.None);
        capture.SetResult("cp-0");
        await Task.WhenAll(first, second);
        session.Say(new TurnStarted { TurnId = "t1" });
        session.Say(new TurnCompleted(TurnOutcome.Completed) { TurnId = "t1" });
        await host.DisposeAsync();

        Assert.Equal(["one", "two"], session.Sent);
        var turn = Assert.Single(host.State.Timeline.OfType<CheckpointEntry>());
        Assert.Equal(("cp-0", "cp-1"), (turn.BaseCheckpointId, turn.Id));
    }

    [Fact]
    public async Task A_message_to_an_agent_whose_process_ended_opens_no_turn()
    {
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), new FakeCheckpoints());
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        session.Say(new SessionStateChanged(AgentSessionState.Failed, "claude exited"));

        await host.ExecuteAsync(new SendMessage("still there?"), CancellationToken.None);

        Assert.False(host.HasSession);
        Assert.Empty(session.Sent);
        Assert.False(host.State.IsWorking);
        Assert.Contains(host.State.Timeline, e => e is NoticeEntry { Text: "The agent is not running." });
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_message_to_an_agent_still_starting_is_neither_recorded_nor_sent()
    {
        var checkpoints = new FakeCheckpoints();
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession { StaysStarting = true };
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await host.ExecuteAsync(new SendMessage("too early"), CancellationToken.None);

        Assert.False(host.HasSession);
        Assert.Empty(session.Sent);
        Assert.Equal(0, checkpoints.Captured);
        Assert.DoesNotContain(host.State.Timeline, e => e is MessageEntry);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Files_are_not_restored_under_a_working_agent()
    {
        var checkpoints = new FakeCheckpoints();
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        session.Say(new TurnStarted { TurnId = "t" });

        await host.ExecuteAsync(new RestoreCheckpoint("cp-0"), CancellationToken.None);

        Assert.Equal(0, checkpoints.Restored);
        Assert.Contains(host.State.Timeline, e => e is NoticeEntry { Level: NoticeLevel.Warning });
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Files_are_not_restored_while_a_turns_baseline_is_being_taken()
    {
        var baseline = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new FakeCheckpoints { FirstCapture = baseline.Task };
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        var send = host.ExecuteAsync(new SendMessage("fix the build"), CancellationToken.None);
        var restore = host.ExecuteAsync(new RestoreCheckpoint("cp-0"), CancellationToken.None);
        baseline.SetResult("cp-0");
        await Task.WhenAll(send, restore).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, checkpoints.Restored);
        Assert.Equal(["fix the build"], session.Sent);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_message_the_session_refuses_closes_the_turn_it_opened()
    {
        var checkpoints = new FakeCheckpoints();
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), checkpoints);
        var session = new FakeSession { FailSend = new TimeoutException("turn/start did not answer") };
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);

        await Xunit.Record.ExceptionAsync(() => host.ExecuteAsync(new SendMessage("fix the build"), CancellationToken.None));
        await checkpoints.Settled();
        await host.ExecuteAsync(new RestoreCheckpoint("cp-0"), CancellationToken.None);

        Assert.False(host.State.IsWorking);
        Assert.Equal(1, checkpoints.Restored);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_session_that_fails_to_start_says_why()
    {
        var host = new AgentConversationHost(Record(), new SqliteConversationStore(_path), null);

        await host.StartAsync(_ => new FakeSession { FailStart = "claude was not found" }, CancellationToken.None);

        Assert.Equal(AgentSessionState.Failed, host.State.SessionState);
        Assert.Equal("claude was not found", Assert.IsType<NoticeEntry>(host.State.Timeline.Single()).Text);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_replaced_session_no_longer_speaks_for_the_conversation()
    {
        var store = new SqliteConversationStore(_path);
        var host = new AgentConversationHost(Record(), store, null);
        var replaced = new FakeSession();
        await host.StartAsync(sink => replaced.Bind(sink), CancellationToken.None);
        var running = new FakeSession();
        await host.StartAsync(sink => running.Bind(sink), CancellationToken.None);

        // The old session's exit watcher reports it, after the new one is already ready.
        replaced.Say(new SessionStateChanged(AgentSessionState.Stopped));

        Assert.True(host.HasSession);
        await host.ExecuteAsync(new SendMessage("hello"), CancellationToken.None);
        Assert.Equal(["hello"], running.Sent);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_shutdown_waits_for_a_conversation_closed_by_its_tile_to_reach_the_store()
    {
        var store = new SqliteConversationStore(_path);
        var session = new FakeSession();
        var host = new AgentConversationHost(Record(), store, null);
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await host.ExecuteAsync(new SendMessage("hello"), CancellationToken.None);
        session.Say(new AssistantMessageCompleted("m", "written before the process left"));

        mTiles.Services.Agents.Sessions.ConversationClosings.Close(host);
        mTiles.Services.Agents.Sessions.ConversationClosings.WaitForAll(TimeSpan.FromSeconds(10));

        var reopened = new AgentConversationHost(Record(), store, null);
        Assert.Contains(reopened.State.Timeline,
            entry => entry is MessageEntry { Text: "written before the process left" });
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task A_conversation_is_reopened_only_after_its_closing_host_has_written_its_last_events()
    {
        var store = new SqliteConversationStore(_path);
        var session = new FakeSession();
        var host = new AgentConversationHost(Record(), store, null);
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        await host.ExecuteAsync(new SendMessage("hello"), CancellationToken.None);
        session.Say(new AssistantMessageCompleted("m", "the closing host's last word"));

        mTiles.Services.Agents.Sessions.ConversationClosings.Close(host);
        await mTiles.Services.Agents.Sessions.ConversationClosings.WhenClosedAsync(host.ConversationId)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var reopened = new AgentConversationHost(Record(), store, null);
        Assert.Contains(reopened.State.Timeline,
            entry => entry is MessageEntry { Text: "the closing host's last word" });
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task Events_a_store_refused_once_are_written_with_the_next_batch()
    {
        var store = new RefusingOnceStore(new SqliteConversationStore(_path));
        var host = new AgentConversationHost(Record(), store, null);
        var session = new FakeSession();
        await host.StartAsync(sink => session.Bind(sink), CancellationToken.None);
        store.RefuseNextAppend = true;
        await host.ExecuteAsync(new SendMessage("keep me"), CancellationToken.None);
        await store.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Say(new AssistantMessageCompleted("m", "after the failure"));
        await host.DisposeAsync();

        var reopened = new AgentConversationHost(Record(), store, null);
        Assert.Contains(reopened.State.Timeline, entry => entry is MessageEntry { Text: "keep me" });
        Assert.Contains(reopened.State.Timeline, entry => entry is MessageEntry { Text: "after the failure" });
        await reopened.DisposeAsync();
    }

    private sealed class RefusingOnceStore(IConversationStore inner) : IConversationStore
    {
        public volatile bool RefuseNextAppend;
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConversationRecord? Find(string conversationId) => inner.Find(conversationId);
        public void Save(ConversationRecord record) => inner.Save(record);
        public IReadOnlyList<AgentEvent> ReadEvents(string conversationId) => inner.ReadEvents(conversationId);
        public long LastSequence(string conversationId) => inner.LastSequence(conversationId);
        public void Delete(string conversationId) => inner.Delete(conversationId);

        public void Append(string conversationId, IReadOnlyList<AgentEvent> events)
        {
            if (RefuseNextAppend)
            {
                RefuseNextAppend = false;
                Refused.TrySetResult();
                throw new IOException("database is locked");
            }

            inner.Append(conversationId, events);
        }
    }

    private sealed class FakeSession : IAgentSession
    {
        private IAgentEventSink? _sink;
        public List<string> Sent { get; } = [];
        public string? FailStart { get; init; }

        public IAgentSession Bind(IAgentEventSink sink)
        {
            _sink = sink;
            return this;
        }

        public void Say(AgentEvent e) => _sink!.Emit(e);

        public bool StaysStarting { get; init; }

        public Task StartAsync(CancellationToken ct)
        {
            if (FailStart is not null) throw new InvalidOperationException(FailStart);
            // A real session announces it is ready once its thread exists.
            if (!StaysStarting) _sink?.Emit(new SessionStateChanged(AgentSessionState.Ready));
            return Task.CompletedTask;
        }

        public Exception? FailSend { get; init; }

        public Task SendAsync(AgentTurnInput input, CancellationToken ct)
        {
            if (FailSend is not null)
            {
                // codex opens the turn and then times out on `turn/start`'s reply.
                _sink?.Emit(new TurnStarted { TurnId = "t" });
                throw FailSend;
            }
            Sent.Add(input.Text);
            return Task.CompletedTask;
        }

        public SettingsChangeOutcome SettingsOutcome { get; init; } = SettingsChangeOutcome.Applied;

        /// <summary>The outcome per change, where a test needs one setting taken and another not.</summary>
        public Func<SessionSettings, SettingsChangeOutcome>? OutcomeFor { get; init; }

        public Task<SettingsChangeOutcome> ChangeSettingsAsync(SessionSettings settings, CancellationToken ct)
        {
            var outcome = OutcomeFor?.Invoke(settings) ?? SettingsOutcome;
            if (outcome == SettingsChangeOutcome.Applied)
                _sink?.Emit(new SessionConfigured(settings.Model, settings.Mode, null, settings.Effort));
            return Task.FromResult(outcome);
        }

        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct) => Task.CompletedTask;
        public Task AnswerQuestionsAsync(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            // A real session reports its process exiting as it is disposed.
            _sink?.Emit(new SessionStateChanged(AgentSessionState.Stopped));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCheckpoints : ITurnCheckpoints
    {
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captured { get; private set; }
        public int Restored { get; private set; }

        public Task Settled() => _second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        /// <summary>What the first photograph answers with, where a test holds it back.</summary>
        public Task<string?>? FirstCapture { get; init; }

        public Task<string?> CaptureAsync(string conversationId, int index, CancellationToken ct)
        {
            Captured++;
            if (Captured == 1 && FirstCapture is not null) return FirstCapture;
            if (Captured == 2) _ = Task.Delay(50).ContinueWith(_ => _second.TrySetResult());
            return Task.FromResult<string?>($"cp-{index}");
        }

        public Task<IReadOnlyList<ChangedFile>> ChangesAsync(string fromCheckpoint, string toCheckpoint, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>([new ChangedFile("a.cs", FileChangeKind.Modified, 1, 1)]);

        public Task<string> DiffAsync(string fromCheckpoint, string toCheckpoint, string? path, CancellationToken ct) =>
            Task.FromResult("");

        public Task<string> RestoreAsync(string checkpoint, CancellationToken ct)
        {
            Restored++;
            return Task.FromResult("before-restore");
        }

        public Task ForgetAsync(string conversationId, CancellationToken ct) => Task.CompletedTask;
    }
}
