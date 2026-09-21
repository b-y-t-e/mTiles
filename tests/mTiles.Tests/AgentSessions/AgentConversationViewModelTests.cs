using System.Collections.ObjectModel;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>The parts of the conversation view that are rules rather than drawing.</summary>
public class AgentConversationViewModelTests
{
    /// <summary>The view calls <c>EnsureStarted</c> on every attach, and a tile re-parented while it is still
    /// starting attaches twice before its host exists; the second call must not start the agent again.</summary>
    [Fact]
    public async Task Attaching_twice_while_starting_opens_the_conversation_once()
    {
        using var settings = new TempSettings();
        var agent = mTiles.Services.Agents.AiAgentCatalog.Find("claude")!;
        var store = new CountingStore(new mTiles.AgentSessions.Storage.SqliteConversationStore(
            Path.Combine(Path.GetTempPath(), $"mtiles-start-{Guid.NewGuid():N}.db")));
        var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service, store,
            mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(agent), agent, () => Guid.NewGuid().ToString(),
            post: action => action());

        vm.EnsureStarted();
        vm.EnsureStarted();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (store.Opened == 0 || vm.IsStarting)
        {
            Assert.True(DateTime.UtcNow < deadline, "The tile never finished starting.");
            await Task.Delay(20);
        }

        await Task.Delay(200);
        vm.Dispose();
        Assert.Equal(1, store.Opened);
    }

    private sealed class CountingStore(mTiles.AgentSessions.Storage.IConversationStore inner)
        : mTiles.AgentSessions.Storage.IConversationStore
    {
        private int _opened;
        public int Opened => _opened;

        public mTiles.AgentSessions.Storage.ConversationRecord? Find(string conversationId) => inner.Find(conversationId);

        public IReadOnlyList<mTiles.AgentSessions.Storage.ConversationSummary> List(string workingDirectory) =>
            inner.List(workingDirectory);

        public void Save(mTiles.AgentSessions.Storage.ConversationRecord record) => inner.Save(record);

        public IReadOnlyList<AgentEvent> ReadEvents(string conversationId)
        {
            Interlocked.Increment(ref _opened);
            return inner.ReadEvents(conversationId);
        }

        public long LastSequence(string conversationId) => inner.LastSequence(conversationId);
        public void Append(string conversationId, IReadOnlyList<AgentEvent> events) => inner.Append(conversationId, events);
        public void Delete(string conversationId) => inner.Delete(conversationId);
    }

    /// <summary>A conversation is an agent working in this workspace, and a process of this machine's:
    /// read as neither, its workspace gets no skills, is never asked about its instruction file, and
    /// reports a memory figure with the heaviest thing in it missing.</summary>
    [Fact]
    public void A_conversation_counts_as_an_agent_in_the_workspace_and_as_a_process()
    {
        Assert.True(typeof(IAgentTile).IsAssignableFrom(typeof(AgentConversationTileViewModel)));
        Assert.True(typeof(IProcessTile).IsAssignableFrom(typeof(AgentConversationTileViewModel)));
        Assert.True(typeof(IAgentTile).IsAssignableFrom(typeof(TerminalAgentTileViewModel)));
    }

    [Fact]
    public void A_streaming_message_is_updated_in_place_not_rebuilt()
    {
        var items = new ObservableCollection<TimelineItemViewModel>();
        var state = ConversationReducer.Replay([new AssistantTextDelta("m", "He")]);
        TimelineSync.Sync(items, state.Timeline, Create);
        var shown = items[0];

        state = ConversationReducer.Apply(state, new AssistantTextDelta("m", "llo"));
        TimelineSync.Sync(items, state.Timeline, Create);

        Assert.Same(shown, items[0]);
        Assert.Equal("Hello", ((MessageItemViewModel)items[0]).Text);
    }

    [Fact]
    public void A_replaced_conversation_drops_the_rows_it_no_longer_has()
    {
        var items = new ObservableCollection<TimelineItemViewModel>();
        TimelineSync.Sync(items,
            ConversationReducer.Replay([new UserMessageAdded("a", "1", []), new UserMessageAdded("b", "2", [])]).Timeline, Create);

        TimelineSync.Sync(items, ConversationState.Empty.Timeline, Create);

        Assert.Empty(items);
    }

    [Fact]
    public void A_group_is_open_for_the_whole_turn_and_folds_when_it_ends_unless_somebody_chose()
    {
        var state = ConversationReducer.Replay([new ToolStarted("t", ToolKind.Command, "Bash", "build", ToolDetail.Empty)]);
        var group = new WorkGroupItemViewModel((WorkGroupEntry)state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.True(group.IsExpanded);

        // Between two tools nothing is running, and that is not the turn finishing: folded here, the
        // list shuts and opens again on every gap in a long turn.
        state = ConversationReducer.Apply(state, new ToolCompleted("t", ToolStatus.Completed));
        group.Update(state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.True(group.IsExpanded);

        // And a tool that starts and finishes between two draws never shows as running at all.
        state = ConversationReducer.Apply(state, new ToolStarted("t2", ToolKind.FileRead, "Read", "a", ToolDetail.Empty));
        state = ConversationReducer.Apply(state, new ToolCompleted("t2", ToolStatus.Completed));
        group.Update(state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.True(group.IsExpanded);

        // The turn ending is the tile's answer, not the group's.
        group.FollowTurn(isTheLiveTurnsWork: false);
        Assert.False(group.IsExpanded);

        group.ToggleCommand.Execute(null);
        group.FollowTurn(isTheLiveTurnsWork: false);
        Assert.True(group.IsExpanded);
    }

    /// <summary>Which group the tile holds open — the running turn's own work, never whichever group
    /// the timeline happens to end with.</summary>
    [Fact]
    public void Only_the_running_turns_own_work_is_live()
    {
        var first = ConversationReducer.Replay(
        [
            new UserMessageAdded("m1", "build it", []) { TurnId = "turn-1" },
            new TurnStarted { TurnId = "turn-1" },
            new ToolStarted("t", ToolKind.Command, "Bash", "build", ToolDetail.Empty) { TurnId = "turn-1" },
            new ToolCompleted("t", ToolStatus.Completed) { TurnId = "turn-1" },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "turn-1" },
        ]);
        var firstGroup = first.Timeline.OfType<WorkGroupEntry>().Single();
        Assert.False(LiveTurnWork.IsLive(firstGroup, first));

        // The next turn has begun and has run no tool yet, so it has no group of its own: the last
        // group in the timeline is the finished turn's and must stay folded.
        var second = ConversationReducer.Apply(first, new UserMessageAdded("m2", "and again", []) { TurnId = "turn-2" });
        second = ConversationReducer.Apply(second, new TurnStarted { TurnId = "turn-2" });
        Assert.False(LiveTurnWork.IsLive(firstGroup, second));

        second = ConversationReducer.Apply(second,
            new ToolStarted("u", ToolKind.FileRead, "Read", "a", ToolDetail.Empty) { TurnId = "turn-2" });
        var groups = second.Timeline.OfType<WorkGroupEntry>().ToList();
        Assert.Equal(2, groups.Count);
        Assert.False(LiveTurnWork.IsLive(groups[0], second));
        Assert.True(LiveTurnWork.IsLive(groups[1], second));
    }

    /// <summary>The tile is what tells a group the turn is over, and that wiring is the whole feature:
    /// without it a group never opens during its turn and never folds after it.</summary>
    [Fact]
    public void Drawing_holds_the_running_turns_work_open_and_folds_it_when_the_turn_ends()
    {
        using var settings = new TempSettings();
        var agent = mTiles.Services.Agents.AiAgentCatalog.Find("claude")!;
        using var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
            new mTiles.AgentSessions.Storage.SqliteConversationStore(
                Path.Combine(Path.GetTempPath(), $"mtiles-turn-{Guid.NewGuid():N}.db")),
            mTiles.Services.Agents.AiAgentCatalog.SeedInstanceFor(agent), agent, () => "tile",
            post: action => action());

        var running = ConversationReducer.Replay(
        [
            new UserMessageAdded("m1", "build it", []) { TurnId = "turn-1" },
            new TurnStarted { TurnId = "turn-1" },
            new ToolStarted("t", ToolKind.Command, "Bash", "build", ToolDetail.Empty) { TurnId = "turn-1" },
            new ToolCompleted("t", ToolStatus.Completed) { TurnId = "turn-1" },
        ]);
        vm.Draw(running);
        Assert.True(vm.Timeline.OfType<WorkGroupItemViewModel>().Single().IsExpanded);

        var finished = ConversationReducer.Apply(running, new TurnCompleted(TurnOutcome.Completed) { TurnId = "turn-1" });
        vm.Draw(finished);
        Assert.False(vm.Timeline.OfType<WorkGroupItemViewModel>().Single().IsExpanded);
    }

    [Fact]
    public void A_group_says_what_the_work_was_in_the_fewest_words()
    {
        var at = DateTimeOffset.UtcNow;
        WorkItem[] items =
        [
            new ToolCallItem("1", ToolKind.Command, "Bash", "a", ToolDetail.Empty, "", ToolCallState.Completed, at, at),
            new ToolCallItem("2", ToolKind.Command, "Bash", "b", ToolDetail.Empty, "", ToolCallState.Failed, at, at),
            new ToolCallItem("3", ToolKind.FileChange, "Edit", "c", ToolDetail.Empty, "", ToolCallState.Completed, at, at),
            new ReasoningItem("4", "hmm"),
        ];

        Assert.Equal("2 commands · 1 edit · 1 thought · 1 failed", WorkGroupItemViewModel.SummaryOf(items));
    }

    [Theory]
    [InlineData(42_100L, 200_000L, null, "42.1k / 200k tokens")]
    [InlineData(900L, null, 0.31, "900 tokens · $0.31")]
    [InlineData(null, null, null, "")]
    public void Usage_says_whatever_the_agent_said_and_nothing_it_did_not(long? used, long? window, double? cost, string expected) =>
        Assert.Equal(expected, AgentConversationTileViewModel.UsageDisplay(
            new TokenUsage(used, window, CostUsd: cost is null ? null : (decimal)cost.Value)));

    [Fact]
    public void A_diff_is_read_into_lines_worth_colouring()
    {
        var lines = DiffLines.Parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same");

        Assert.Equal(
            [DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Hunk, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context],
            lines.Select(line => line.Kind));
    }

    private static TimelineItemViewModel Create(TimelineEntry entry) => entry switch
    {
        MessageEntry message => new MessageItemViewModel(message),
        WorkGroupEntry group => new WorkGroupItemViewModel(group),
        _ => new NoticeItemViewModel(new NoticeEntry(entry.Id, NoticeLevel.Info, "")),
    };
}
