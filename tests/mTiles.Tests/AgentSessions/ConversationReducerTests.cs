using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>How a conversation's events become what is drawn — see <see cref="ConversationReducer"/>.</summary>
public class ConversationReducerTests
{
    private static ConversationState Play(params AgentEvent[] events) => ConversationReducer.Replay(events);

    [Fact]
    public void Streamed_text_is_one_message_and_its_completion_replaces_it()
    {
        var state = Play(
            new AssistantTextDelta("m1", "Hel"),
            new AssistantTextDelta("m1", "lo"),
            new AssistantMessageCompleted("m1", "Hello."));

        var message = Assert.IsType<MessageEntry>(Assert.Single(state.Timeline));
        Assert.Equal("Hello.", message.Text);
        Assert.False(message.IsStreaming);
    }

    [Fact]
    public void A_completion_with_no_text_keeps_what_was_streamed()
    {
        var state = Play(new AssistantTextDelta("m1", "partial"), new AssistantMessageCompleted("m1", ""));

        Assert.Equal("partial", Assert.IsType<MessageEntry>(Assert.Single(state.Timeline)).Text);
    }

    [Fact]
    public void Work_between_two_messages_is_one_group_and_a_message_closes_it()
    {
        var state = Play(
            new UserMessageAdded("u1", "fix it", []),
            new ToolStarted("t1", ToolKind.FileRead, "Read", "Read a.cs", ToolDetail.Empty),
            new ReasoningDelta("r1", "thinking"),
            new ToolStarted("t2", ToolKind.Command, "Bash", "dotnet build", new ToolDetail(Command: "dotnet build")),
            new AssistantMessageCompleted("m1", "Done."),
            new ToolStarted("t3", ToolKind.FileChange, "Edit", "Edit b.cs", ToolDetail.Empty));

        Assert.Collection(state.Timeline,
            e => Assert.IsType<MessageEntry>(e),
            e => Assert.Equal(3, Assert.IsType<WorkGroupEntry>(e).Items.Count),
            e => Assert.IsType<MessageEntry>(e),
            e => Assert.Single(Assert.IsType<WorkGroupEntry>(e).Items));
    }

    [Fact]
    public void A_tool_is_updated_where_it_is_and_its_output_is_replaced_by_the_whole_output()
    {
        var state = Play(
            new ToolStarted("t1", ToolKind.Command, "Bash", "Bash", ToolDetail.Empty),
            new ToolUpdated("t1", "npm test", new ToolDetail(Command: "npm test")),
            new ToolUpdated("t1", OutputDelta: "ok 1\n"),
            new ToolUpdated("t1", OutputDelta: "ok 2\n"),
            new ToolCompleted("t1", ToolStatus.Failed, "whole output", new ToolDetail(ExitCode: 1)));

        var tool = Assert.IsType<ToolCallItem>(Assert.Single(Assert.IsType<WorkGroupEntry>(Assert.Single(state.Timeline)).Items));
        Assert.Equal("npm test", tool.Title);
        Assert.Equal("npm test", tool.Detail.Command);
        Assert.Equal(1, tool.Detail.ExitCode);
        Assert.Equal("whole output", tool.Output);
        Assert.Equal(ToolCallState.Failed, tool.State);
    }

    [Fact]
    public void A_repeated_start_refreshes_the_tool_rather_than_doubling_it()
    {
        var state = Play(
            new ToolStarted("t1", ToolKind.Other, "x", "first", ToolDetail.Empty),
            new ToolStarted("t1", ToolKind.Command, "x", "second", new ToolDetail(Command: "ls")));

        var tool = Assert.IsType<ToolCallItem>(Assert.Single(Assert.IsType<WorkGroupEntry>(Assert.Single(state.Timeline)).Items));
        Assert.Equal("second", tool.Title);
        Assert.Equal(ToolKind.Command, tool.Kind);
    }

    [Fact]
    public void Events_about_things_that_do_not_exist_are_ignored()
    {
        var state = Play(
            new ToolCompleted("missing", ToolStatus.Completed),
            new ApprovalResolved("missing", ApprovalDecision.Accept),
            new QuestionsAnswered("missing", null),
            new CheckpointRestored("missing"));

        Assert.Single(state.Timeline); // only the notice the restore says
        Assert.IsType<NoticeEntry>(state.Timeline[0]);
    }

    [Fact]
    public void An_answered_approval_leaves_the_pending_list_and_is_recorded_beside_its_tool()
    {
        var state = Play(
            new ToolStarted("t1", ToolKind.Command, "Bash", "rm -rf build", ToolDetail.Empty),
            new AssistantMessageCompleted("m1", "about to delete"),
            new ApprovalRequested("a1", ApprovalKind.Command, "rm -rf build", null, "t1",
                [new ApprovalOption(ApprovalDecision.Accept, "Allow")]));

        Assert.Single(state.PendingApprovals);
        Assert.True(state.IsWaitingForUser);

        state = ConversationReducer.Apply(state, new ApprovalResolved("a1", ApprovalDecision.Decline));

        Assert.Empty(state.PendingApprovals);
        var group = Assert.IsType<WorkGroupEntry>(state.Timeline[0]);
        Assert.Contains(group.Items, item => item is DecisionItem { Decision: ApprovalDecision.Decline });
    }

    [Fact]
    public void A_turn_that_ends_closes_everything_it_left_open()
    {
        var state = Play(
            new TurnStarted { TurnId = "turn1" },
            new AssistantTextDelta("m1", "working on"),
            new ToolStarted("t1", ToolKind.Command, "Bash", "long build", ToolDetail.Empty),
            new ApprovalRequested("a1", ApprovalKind.Command, "x", null, null, [new ApprovalOption(ApprovalDecision.Accept, "Allow")]),
            new QuestionsAsked("q1", [new UserQuestion("q", null, "which?", [], false, true)]),
            new TurnCompleted(TurnOutcome.Interrupted) { TurnId = "turn1" });

        Assert.Null(state.ActiveTurnId);
        Assert.Empty(state.PendingApprovals);
        Assert.Empty(state.PendingQuestions);
        Assert.False(Assert.IsType<MessageEntry>(state.Timeline[0]).IsStreaming);
        Assert.Equal(ToolCallState.Abandoned,
            Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(state.Timeline[1]).Items[0]).State);
        Assert.Equal("Interrupted.", Assert.IsType<NoticeEntry>(state.Timeline[^1]).Text);
    }

    [Fact]
    public void A_session_whose_process_has_gone_closes_what_its_turn_left_open()
    {
        var state = Play(
            new TurnStarted { TurnId = "turn1" },
            new ToolStarted("t1", ToolKind.Command, "Bash", "build", ToolDetail.Empty),
            new SessionStateChanged(AgentSessionState.Failed, "exit 3"));

        Assert.False(state.IsWorking);
        Assert.Equal("exit 3", Assert.IsType<NoticeEntry>(state.Timeline[^1]).Text);
        Assert.Equal(ToolCallState.Abandoned,
            Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(state.Timeline[0]).Items[0]).State);
    }

    [Fact]
    public void An_answered_round_of_questions_is_kept_where_it_was_asked()
    {
        var state = Play(
            new QuestionsAsked("q1", [new UserQuestion("color", null, "Which colour?", [new QuestionOption("Red")], false, true)]),
            new QuestionsAnswered("q1", new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["Red"] }));

        var record = Assert.IsType<QuestionsEntry>(Assert.Single(state.Timeline));
        Assert.Equal(["Red"], record.Answers!["color"]);
        Assert.Empty(state.PendingQuestions);
    }

    [Fact]
    public void Usage_reports_are_merged_and_a_missing_figure_keeps_the_last_one()
    {
        var state = Play(
            new UsageUpdated(new TokenUsage(1000, 200_000)),
            new UsageUpdated(new TokenUsage(null, null, CostUsd: 0.12m)));

        Assert.Equal(1000, state.Usage!.UsedTokens);
        Assert.Equal(200_000, state.Usage.ContextWindow);
        Assert.Equal(0.12m, state.Usage.CostUsd);
    }

    [Fact]
    public void Only_a_checkpoint_that_closes_a_turn_and_changed_something_is_drawn()
    {
        var file = new ChangedFile("a.cs", FileChangeKind.Modified, 3, 1);
        var state = Play(
            new CheckpointCaptured("c0", null, []),
            new CheckpointCaptured("c1", "c0", []),
            new CheckpointCaptured("c2", "c1", [file]));

        var entry = Assert.IsType<CheckpointEntry>(Assert.Single(state.Timeline));
        Assert.Equal("c1", entry.BaseCheckpointId);
        Assert.Equal("c2", state.LatestCheckpointId);
    }

    [Fact]
    public void Restoring_the_start_of_a_turn_marks_that_turn()
    {
        var state = Play(
            new CheckpointCaptured("c2", "c1", [new ChangedFile("a.cs", FileChangeKind.Added, 1, 0)]),
            new CheckpointRestored("c1"));

        Assert.True(Assert.IsType<CheckpointEntry>(state.Timeline[0]).Restored);
        Assert.IsType<NoticeEntry>(state.Timeline[1]);
    }

    [Fact]
    public void Restoring_the_start_of_a_turn_marks_every_later_turn_too()
    {
        var file = new ChangedFile("a.cs", FileChangeKind.Modified, 1, 1);
        var state = Play(
            new CheckpointCaptured("c1", "c0", [file]),
            new CheckpointCaptured("c3", "c2", [file]),
            new CheckpointRestored("c0"));

        Assert.All(state.Timeline.OfType<CheckpointEntry>(), entry => Assert.True(entry.Restored));
    }

    [Fact]
    public void Restoring_a_later_turn_leaves_the_earlier_one_undoable()
    {
        var file = new ChangedFile("a.cs", FileChangeKind.Modified, 1, 1);
        var state = Play(
            new CheckpointCaptured("c1", "c0", [file]),
            new CheckpointCaptured("c3", "c2", [file]),
            new CheckpointRestored("c2"));

        var entries = state.Timeline.OfType<CheckpointEntry>().ToList();
        Assert.Equal([false, true], entries.Select(e => e.Restored));
    }

    [Fact]
    public void A_failed_turn_says_why()
    {
        var state = Play(new TurnStarted { TurnId = "t" }, new TurnCompleted(TurnOutcome.Failed, "rate limited") { TurnId = "t" });

        var notice = Assert.IsType<NoticeEntry>(Assert.Single(state.Timeline));
        Assert.Equal(NoticeLevel.Error, notice.Level);
        Assert.Equal("rate limited", notice.Text);
    }

    [Fact]
    public void Replaying_the_same_events_gives_the_same_state()
    {
        AgentEvent[] events =
        [
            new UserMessageAdded("u", "hi", []) { Sequence = 1 },
            new TurnStarted { TurnId = "t", Sequence = 2 },
            new ToolStarted("x", ToolKind.Search, "Grep", "Search TODO", ToolDetail.Empty) { Sequence = 3 },
            new AssistantMessageCompleted("m", "found none") { Sequence = 4 },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "t", Sequence = 5 },
        ];

        var first = ConversationReducer.Replay(events);
        var second = ConversationReducer.Replay(events);

        Assert.Equal(first.Timeline.Select(e => e.Id), second.Timeline.Select(e => e.Id));
        Assert.Equal(5, first.LastSequence);
    }
}
