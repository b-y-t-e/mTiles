using System.Text.Json;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Hosting;
using mTiles.AgentSessions.Storage;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>What survives a restart: the SQLite store, the JSON a browser will read, and the batching
/// that keeps a streamed reply from being a thousand rows.</summary>
public class ConversationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mtiles-store-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    [Fact]
    public void Every_kind_of_event_goes_in_and_comes_back_as_itself()
    {
        var store = new SqliteConversationStore(_path);
        var at = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        AgentEvent[] events =
        [
            new SessionConfigured("model", "plan", "token"),
            new UserMessageAdded("u", "hi", [new ImageAttachment("image/png", "AAA=")]),
            new ToolStarted("t", ToolKind.FileChange, "Edit", "Edit a.cs", new ToolDetail(Paths: ["a.cs"], Diff: "+x")),
            new ApprovalRequested("a", ApprovalKind.Command, "rm", "rm -rf", "t", [new ApprovalOption(ApprovalDecision.AcceptForSession, "Always")]),
            new QuestionsAnswered("q", new Dictionary<string, IReadOnlyList<string>> { ["x"] = ["y"] }),
            new CheckpointCaptured("c2", "c1", [new ChangedFile("a.cs", FileChangeKind.Added, 2, 0)]),
            new UsageUpdated(new TokenUsage(10, 20, CostUsd: 1.5m)),
            new TurnCompleted(TurnOutcome.Failed, "boom"),
        ];
        var numbered = events.Select((e, i) => e with { Sequence = i + 1, At = at, TurnId = "turn" }).ToList();

        store.Save(new ConversationRecord("conv", "claude", "/w", null, at, at));
        store.Append("conv", numbered);

        var read = store.ReadEvents("conv");
        Assert.Equal(numbered.Select(e => e.GetType()), read.Select(e => e.GetType()));
        Assert.Equal(numbered.Select(e => e.Sequence), read.Select(e => e.Sequence));
        Assert.Equal("+x", Assert.IsType<ToolStarted>(read[2]).Detail.Diff);
        Assert.Equal(ApprovalDecision.AcceptForSession, Assert.IsType<ApprovalRequested>(read[3]).Options[0].Decision);
        Assert.Equal("c1", Assert.IsType<CheckpointCaptured>(read[5]).BaseCheckpointId);
        Assert.Equal("turn", read[7].TurnId);
    }

    [Fact]
    public void A_record_is_updated_in_place_and_deleting_takes_its_events()
    {
        var store = new SqliteConversationStore(_path);
        var now = DateTimeOffset.UtcNow;
        store.Save(new ConversationRecord("c", "codex", "/w", null, now, now));
        store.Save(new ConversationRecord("c", "codex", "/w", "thread-1", now, now));
        store.Append("c", [new NoticeRaised(NoticeLevel.Info, "x") { Sequence = 1 }]);

        Assert.Equal("thread-1", store.Find("c")!.ResumeToken);

        store.Delete("c");
        Assert.Null(store.Find("c"));
        Assert.Empty(store.ReadEvents("c"));
    }

    [Fact]
    public void An_event_this_build_cannot_read_is_skipped_rather_than_losing_the_conversation()
    {
        var store = new SqliteConversationStore(_path);
        store.Append("c", [new NoticeRaised(NoticeLevel.Info, "kept") { Sequence = 2 }]);

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO events (conversation_id, sequence, type, at, payload) VALUES ('c', 1, 'Future', '', '{\"type\":\"from.the.future\"}')";
            command.ExecuteNonQuery();
        }

        Assert.Equal("kept", Assert.IsType<NoticeRaised>(Assert.Single(store.ReadEvents("c"))).Text);
    }

    /// <summary>The JSON a browser will be sent names each record the way the C# does.</summary>
    [Fact]
    public void Events_and_commands_are_written_with_a_type_a_browser_can_switch_on()
    {
        var eventJson = JsonSerializer.Serialize<AgentEvent>(new ToolStarted("t", ToolKind.Command, "Bash", "ls", ToolDetail.Empty),
            AgentSessionJson.Options);
        var commandJson = JsonSerializer.Serialize<AgentCommand>(new RespondToApproval("a", ApprovalDecision.Decline),
            AgentSessionJson.Options);

        Assert.Contains("\"type\":\"tool.started\"", eventJson);
        Assert.Contains("\"kind\":\"Command\"", eventJson);
        Assert.Contains("\"type\":\"approval.respond\"", commandJson);
        Assert.Contains("\"decision\":\"Decline\"", commandJson);
        Assert.IsType<RespondToApproval>(JsonSerializer.Deserialize<AgentCommand>(commandJson, AgentSessionJson.Options));
    }

    [Fact]
    public void Consecutive_deltas_of_one_message_are_stored_as_one()
    {
        AgentEvent[] batch =
        [
            new AssistantTextDelta("m", "He") { Sequence = 1 },
            new AssistantTextDelta("m", "llo") { Sequence = 2 },
            new ToolUpdated("t", OutputDelta: "a") { Sequence = 3 },
            new ToolUpdated("t", OutputDelta: "b") { Sequence = 4 },
            new AssistantTextDelta("m", "!") { Sequence = 5 },
            new AssistantTextDelta("other", "x") { Sequence = 6 },
        ];

        var stored = EventBatch.Coalesce(batch);

        Assert.Collection(stored,
            e => Assert.Equal(("Hello", 2L), (((AssistantTextDelta)e).Delta, e.Sequence)),
            e => Assert.Equal("ab", ((ToolUpdated)e).OutputDelta),
            e => Assert.Equal("!", ((AssistantTextDelta)e).Delta),
            e => Assert.Equal("other", ((AssistantTextDelta)e).MessageId));
    }
}
