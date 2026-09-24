using System.Collections.ObjectModel;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
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
        var store = new TestStore();
        using var vm = ConversationTiles.New(settings, store: store);

        vm.EnsureStarted();
        vm.EnsureStarted();
        await ConversationTiles.WaitUntil(() => store.Reads > 0 && !vm.IsStarting, "the tile to finish starting");

        Assert.Equal(1, store.Reads);
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
    public void A_group_stays_folded_and_its_line_says_what_the_turn_is_doing_now()
    {
        var state = ConversationReducer.Replay([new ToolStarted("t", ToolKind.Command, "Bash", "build", ToolDetail.Empty)]);
        var group = new WorkGroupItemViewModel((WorkGroupEntry)state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);

        // Folded, live turn or not: what a running turn owes the reader is one line, not thirty rows.
        Assert.False(group.IsExpanded);
        Assert.Equal("build", group.Headline);

        // Between two tools nothing is running, so the line falls back to what the work came to.
        state = ConversationReducer.Apply(state, new ToolCompleted("t", ToolStatus.Completed));
        group.Update(state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.Equal(group.Summary, group.Headline);

        // The newest running tool is the one shown: an agent that runs several started them in that order.
        state = ConversationReducer.Apply(state, new ToolStarted("t2", ToolKind.FileRead, "Read", "a", ToolDetail.Empty));
        state = ConversationReducer.Apply(state, new ToolStarted("t3", ToolKind.Command, "Bash", "test", ToolDetail.Empty));
        group.Update(state.Timeline[0]);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.Equal("test", group.Headline);

        // The turn ending is the tile's answer, not the group's - and nothing is running afterwards,
        // however the tools were left.
        group.FollowTurn(isTheLiveTurnsWork: false);
        Assert.Equal(group.Summary, group.Headline);

        // Opened by hand, the running tool is a row of its own, so the line above is the tally again.
        group.ToggleCommand.Execute(null);
        group.FollowTurn(isTheLiveTurnsWork: true);
        Assert.True(group.IsExpanded);
        Assert.Equal(group.Summary, group.Headline);
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

    /// <summary>The tile is what tells a group whether its turn is still going, and that wiring is the
    /// whole feature: without it a folded group never says what the agent is doing.</summary>
    [Fact]
    public void Drawing_tells_a_group_whether_its_turn_is_still_going()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        var running = ConversationReducer.Replay(
        [
            new UserMessageAdded("m1", "build it", []) { TurnId = "turn-1" },
            new TurnStarted { TurnId = "turn-1" },
            new ToolStarted("t", ToolKind.Command, "Bash", "build", ToolDetail.Empty) { TurnId = "turn-1" },
        ]);
        vm.Draw(running);
        var group = vm.Timeline.OfType<WorkGroupItemViewModel>().Single();
        Assert.False(group.IsExpanded);
        Assert.Equal("build", group.Headline);

        var finished = ConversationReducer.Apply(running, new ToolCompleted("t", ToolStatus.Completed) { TurnId = "turn-1" });
        finished = ConversationReducer.Apply(finished, new TurnCompleted(TurnOutcome.Completed) { TurnId = "turn-1" });
        vm.Draw(finished);
        group = vm.Timeline.OfType<WorkGroupItemViewModel>().Single();
        Assert.Equal(group.Summary, group.Headline);
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

    private static TimelineItemViewModel Create(TimelineEntry entry) => entry switch
    {
        MessageEntry message => new MessageItemViewModel(message),
        WorkGroupEntry group => new WorkGroupItemViewModel(group),
        _ => new NoticeItemViewModel(new NoticeEntry(entry.Id, NoticeLevel.Info, "")),
    };
}
