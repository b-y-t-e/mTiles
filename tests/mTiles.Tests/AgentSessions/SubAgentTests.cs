using System.Text.Json;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Agents.Sessions.Claude;
using mTiles.Services.Agents.Sessions.Codex;
using mTiles.Services.Agents.Sessions.OpenCode;
using mTiles.Services.Providers;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// A sub-agent at work keeps the tile busy, whether or not a turn is open.
/// </summary>
/// <remarks>The Claude Code lines are a recording made 2026-09-24 against 2.1.281 (a background <c>Agent</c>
/// call, trimmed of fields nothing reads); the codex notifications are t3code's capture from codex-cli 0.145.0.
/// </remarks>
public class SubAgentTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    /// <summary>What follows once the turn that launched it has ended.</summary>
    public static TheoryData<string, AgentEvent, bool> WhatEndsASubAgent => new()
    {
        { "progress is not an end", new SubAgentProgressed("s", "Reading README.md"), true },
        { "nor is a later turn ending", new TurnCompleted(TurnOutcome.Completed) { TurnId = "t2" }, true },
        { "it ends when it says so", new SubAgentEnded("s", SubAgentOutcome.Completed), false },
        { "and with the session", new SessionStateChanged(AgentSessionState.Stopped), false },
    };

    [Theory]
    [MemberData(nameof(WhatEndsASubAgent))]
    public void A_background_sub_agent_keeps_the_conversation_busy_until_it_ends(string why, AgentEvent then, bool busy)
    {
        var state = ConversationReducer.Replay(
        [
            new TurnStarted { TurnId = "t" },
            new SubAgentStarted("s", "count headings", "call", Background: true) { TurnId = "t" },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" },
            then,
        ]);

        Assert.True(busy == state.IsBusy, why);
    }

    [Fact]
    public void A_foreground_sub_agent_ends_with_its_turn()
    {
        var state = ConversationReducer.Replay(
        [
            new TurnStarted { TurnId = "t" },
            new SubAgentStarted("s", "review", "call") { TurnId = "t" },
            new TurnCompleted(TurnOutcome.Interrupted) { TurnId = "t" },
        ]);

        Assert.False(state.IsBusy);
        Assert.Equal(SubAgentStatus.Stopped, state.SubAgents.Single().Status);
    }

    /// <summary>The turn's end drops what the turn asked and nothing a sub-agent still working is waiting on.
    /// </summary>
    [Fact]
    public void A_sub_agent_s_request_outlives_the_turn_and_goes_with_the_sub_agent()
    {
        ApprovalOption[] allow = [new(ApprovalDecision.Accept, "Allow")];
        var state = ConversationReducer.Replay(
        [
            new TurnStarted { TurnId = "t" },
            new SubAgentStarted("s", "writer", Background: true),
            new ApprovalRequested("mine", ApprovalKind.Command, "Run", null, null, allow) { TurnId = "t" },
            new ApprovalRequested("its", ApprovalKind.FileChange, "Write", null, null, allow) { SubAgentId = "s" },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" },
        ]);
        Assert.Equal("its", state.PendingApprovals.Single().RequestId);

        state = ConversationReducer.Apply(state, new SubAgentEnded("s", SubAgentOutcome.Stopped));
        Assert.Empty(state.PendingApprovals);
    }

    /// <summary>A request from a sub-agent not announced yet stays too — the session keeps waiting on it — and
    /// only the session's end drops it.</summary>
    [Fact]
    public void A_request_from_a_sub_agent_not_announced_yet_outlives_the_turn()
    {
        ApprovalOption[] allow = [new(ApprovalDecision.Accept, "Allow")];
        var state = ConversationReducer.Replay(
        [
            new TurnStarted { TurnId = "t" },
            new ApprovalRequested("its", ApprovalKind.FileChange, "Write", null, null, allow) { SubAgentId = "s" },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" },
        ]);
        Assert.Equal("its", state.PendingApprovals.Single().RequestId);

        state = ConversationReducer.Apply(state, new SessionStateChanged(AgentSessionState.Stopped));
        Assert.Empty(state.PendingApprovals);
    }

    /// <summary>Whichever arrives first, the call's row ends up carrying the sub-agent it launched.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_call_that_launched_a_sub_agent_carries_it(bool callFirst)
    {
        AgentEvent call = new ToolStarted("call", ToolKind.SubAgent, "Agent", "count headings", ToolDetail.Empty);
        AgentEvent started = new SubAgentStarted("s", "count headings", "call", Background: true);
        var state = ConversationReducer.Replay(callFirst ? [call, started] : [started, call]);
        state = ConversationReducer.Apply(state, new ToolCompleted("call", ToolStatus.Completed, "launched"));
        state = ConversationReducer.Apply(state, new SubAgentProgressed("s", "Reading README.md"));

        var row = Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(state.Timeline.Single()).Items.Single());
        Assert.Equal((SubAgentStatus.Working, "Reading README.md"), (row.SubAgent!.Status, row.SubAgent.Progress));
    }

    [Fact]
    public void Claude_Code_s_task_lines_are_a_sub_agent_and_its_shells_are_not()
    {
        var mapper = new ClaudeStreamMapper();
        string[] lines =
        [
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"count headings","run_in_background":true}}]},"parent_tool_use_id":null}""",
            """{"type":"system","subtype":"task_started","task_id":"a36","tool_use_id":"toolu_A","description":"count headings","subagent_type":"general-purpose","is_backgrounded":true,"task_type":"local_agent"}""",
            """{"type":"user","message":{"content":[{"tool_use_id":"toolu_A","type":"tool_result","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"parent_tool_use_id":null}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"LAUNCHED"}""",
            """{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"a sub-agent's inside"}]},"parent_tool_use_id":"toolu_A"}""",
            """{"type":"system","subtype":"task_progress","task_id":"a36","tool_use_id":"toolu_A","description":"Reading README.md","last_tool_name":"Read"}""",
            """{"type":"system","subtype":"task_started","task_id":"b6o","owned_by_subagent":true,"tool_use_id":"toolu_B","description":"ping","is_backgrounded":false,"task_type":"local_bash"}""",
        ];
        var events = lines.SelectMany(line => mapper.Map(Json(line), null)).ToList();
        var working = ConversationReducer.Replay(events);

        var only = Assert.Single(working.SubAgents);
        Assert.Equal(("a36", "toolu_A", true, "Reading README.md"), (only.Id, only.ToolCallId, only.Background, only.Progress));
        Assert.DoesNotContain(working.Timeline, e => e is MessageEntry { Text: "a sub-agent's inside" });

        var done = mapper.Map(Json(
            """{"type":"system","subtype":"task_notification","task_id":"a36","tool_use_id":"toolu_A","status":"completed","summary":"40"}"""),
            null);
        var finished = ConversationReducer.Replay(events.Concat(done));
        Assert.Equal((SubAgentStatus.Completed, "40"), (finished.SubAgents.Single().Status, finished.SubAgents.Single().Result));
        Assert.False(finished.IsBusy);
    }

    /// <summary>The agent waking by itself when a background sub-agent finishes is a turn: drawn with a
    /// spinner, closed by its own result — and a sub-agent's line is not the agent waking.</summary>
    [Fact]
    public void Claude_Code_answering_a_finished_sub_agent_by_itself_opens_a_turn_of_its_own()
    {
        var sink = new RecordingSink();
        var session = new ClaudeStreamSession(Launch("claude"), AiAgentCatalog.Find("claude")!, sink);

        session.OnLine("""{"type":"assistant","message":{"id":"s","content":[{"type":"text","text":"inside"}]},"parent_tool_use_id":"toolu_A"}""");
        Assert.DoesNotContain(sink.Events, e => e is TurnStarted);

        session.OnLine("""{"type":"assistant","message":{"id":"m3","content":[{"type":"text","text":"It found 40."}]},"parent_tool_use_id":null}""");
        session.OnLine("""{"type":"result","subtype":"success","is_error":false,"result":"It found 40."}""");

        var state = ConversationReducer.Replay(sink.Events);
        Assert.Equal(1, sink.Events.Count(e => e is TurnStarted));
        Assert.Contains(sink.Events, e => e is TurnCompleted);
        Assert.False(state.IsWorking);
        Assert.Contains(state.Timeline, e => e is MessageEntry { Text: "It found 40." });
    }

    [Fact]
    public void A_codex_sub_agent_s_thread_is_followed_and_never_ends_our_turn()
    {
        var sink = new RecordingSink();
        var session = new CodexAppServerSession(Launch("codex"), (CodexAgent)AiAgentCatalog.Find("codex")!, sink)
        {
            ThreadId = "root",
        };
        (string Method, string Params)[] notifications =
        [
            ("turn/started", """{"threadId":"root","turn":{"id":"rt1","status":"inProgress"}}"""),
            ("item/completed", """{"threadId":"root","item":{"type":"subAgentActivity","id":"c1","kind":"started","agentThreadId":"child","agentPath":"/root/alpha"}}"""),
            ("turn/started", """{"threadId":"child","turn":{"id":"ct1","status":"inProgress"}}"""),
            // A child reporting back names our thread: that is not a sub-agent of ours.
            ("item/completed", """{"threadId":"child","item":{"type":"subAgentActivity","id":"c2","kind":"interacted","agentThreadId":"root","agentPath":"/root"}}"""),
            ("item/started", """{"threadId":"child","item":{"type":"commandExecution","id":"x","command":"dotnet test"}}"""),
            ("turn/completed", """{"threadId":"root","turn":{"id":"rt1","status":"completed"}}"""),
        ];
        foreach (var (method, parameters) in notifications) session.OnNotification(method, Json(parameters));

        var state = ConversationReducer.Replay(sink.Events);
        Assert.False(state.IsWorking);
        Assert.True(state.IsBusy);
        Assert.Equal(("alpha", "dotnet test"), (state.SubAgents.Single().Title, state.SubAgents.Single().Progress));

        session.OnNotification("turn/completed", Json("""{"threadId":"child","turn":{"id":"ct1","status":"completed"}}"""));
        Assert.False(ConversationReducer.Replay(sink.Events).IsBusy);
    }

    /// <summary>Opened by the turn/start of ours or not, a turn codex runs is a turn on screen.</summary>
    [Fact]
    public void A_codex_turn_nobody_sent_a_message_for_is_still_a_turn()
    {
        var sink = new RecordingSink();
        var session = new CodexAppServerSession(Launch("codex"), (CodexAgent)AiAgentCatalog.Find("codex")!, sink)
        {
            ThreadId = "root",
        };

        session.OnNotification("turn/started", Json("""{"threadId":"root","turn":{"id":"rt2"}}"""));
        Assert.True(ConversationReducer.Replay(sink.Events).IsWorking);
        session.OnNotification("turn/completed", Json("""{"threadId":"root","turn":{"id":"rt2","status":"completed"}}"""));
        Assert.False(ConversationReducer.Replay(sink.Events).IsWorking);
    }

    /// <summary>The wiring: the tile reads the conversation's busy, not its turn, for everything that says
    /// the agent is at work — and keeps Send, since a message sent now is an ordinary one.</summary>
    [Fact]
    public void The_tile_stays_at_work_while_only_a_sub_agent_is()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        vm.Draw(ConversationReducer.Replay(
        [
            new SessionStateChanged(AgentSessionState.Ready),
            new TurnStarted { TurnId = "t" },
            new ToolStarted("call", ToolKind.SubAgent, "Agent", "count headings", ToolDetail.Empty) { TurnId = "t" },
            new SubAgentStarted("s", "count headings", "call", Background: true) { TurnId = "t" },
            new ToolCompleted("call", ToolStatus.Completed, "launched") { TurnId = "t" },
            new TurnCompleted(TurnOutcome.Completed) { TurnId = "t" },
            new SubAgentProgressed("s", "Reading README.md"),
        ]));

        Assert.Equal((false, true, true), (vm.IsWorking, vm.IsBusy, vm.HasBackgroundWork));
        Assert.Equal(TileActivity.Working, vm.Activity);
        Assert.Equal("Reading README.md", vm.TurnStageText);
        var group = vm.Timeline.OfType<WorkGroupItemViewModel>().Single();
        Assert.Equal("count headings · Reading README.md", group.Headline);
        var row = (ToolCallItemViewModel)group.Items.Single();
        Assert.Equal(("working…", "Reading README.md"), (row.StateText, row.SubAgentLine));
    }

    private static AgentSessionLaunch Launch(string agentId)
    {
        var agent = AiAgentCatalog.Find(agentId)!;
        return new AgentSessionLaunch(agent.BinaryName, Path.GetTempPath(),
            AgentRuntime.For(new AppSettings(), AiAgentCatalog.SeedInstanceFor(agent), agent: agent),
            new Dictionary<string, string?>(), AiBehaviour.ToolDefault, AiEffort.ToolDefault, null, "c");
    }

    /// <summary>An opencode sub-agent asks under its own session, and the question is ours to answer only when
    /// that session's parents reach ours.</summary>
    public static TheoryData<string, string, bool> WhoseSessionIsIt => new()
    {
        { "our own", "root", true },
        { "a child announced under ours", "child", true },
        { "a grandchild announced under that child", "grandchild", true },
        { "one the stream never saw, whose parent the server names as ours", "unseen", true },
        { "a stranger's", "stranger", false },
    };

    [Theory]
    [MemberData(nameof(WhoseSessionIsIt))]
    public async Task An_opencode_session_is_ours_when_its_parents_reach_ours(string why, string session, bool ours)
    {
        var server = new Dictionary<string, string?> { ["unseen"] = "child", ["stranger"] = "elsewhere" };
        var family = new OpenCodeSessionFamily(id => Task.FromResult<SessionParent?>(new(server.GetValueOrDefault(id)))) { Root = "root" };
        family.Note("child", "root");
        family.Note("grandchild", "child");

        Assert.True(ours == await family.IsOursAsync(session), why);
    }

    [Fact]
    public async Task A_session_known_not_to_be_ours_is_not_asked_about_again()
    {
        var asked = 0;
        var family = new OpenCodeSessionFamily(_ => { asked++; return Task.FromResult<SessionParent?>(new(null)); }) { Root = "root" };

        await family.IsOursAsync("stranger");
        await family.IsOursAsync("stranger");

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_child_announced_under_a_stranger_is_not_ours()
    {
        var family = new OpenCodeSessionFamily(_ => Task.FromResult<SessionParent?>(new(null))) { Root = "root" };
        family.Note("child", "someone-else");

        Assert.False(await family.IsOursAsync("child"));
    }

    [Fact]
    public async Task A_lookup_that_failed_is_asked_again()
    {
        var failing = true;
        var family = new OpenCodeSessionFamily(_ => Task.FromResult(failing ? null : new SessionParent("root")))
        {
            Root = "root",
        };

        Assert.Null(await family.WhoseAsync("child"));
        failing = false;
        Assert.True(await family.WhoseAsync("child"));
    }

    /// <summary>The end of a turn answers the turn's own requests and leaves a sub-agent's waiting.</summary>
    [Fact]
    public void A_sub_agent_s_permission_request_is_not_cancelled_by_the_turn_s_end()
    {
        var sink = new RecordingSink();
        var session = new ClaudeStreamSession(Launch("claude"), AiAgentCatalog.Find("claude")!, sink);

        session.OnLine("""{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Launched."}]},"parent_tool_use_id":null}""");
        session.OnLine("""{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"a.txt","content":"x"},"agent_id":"task1"}}""");
        session.OnLine("""{"type":"result","subtype":"success","is_error":false,"result":"Launched."}""");

        Assert.Contains(sink.Events, e => e is ApprovalRequested { RequestId: "r1", SubAgentId: "task1" });
        Assert.DoesNotContain(sink.Events, e => e is ApprovalResolved { RequestId: "r1" });
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        private readonly Lock _gate = new();
        private readonly List<AgentEvent> _events = [];

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (_gate) return [.. _events];
            }
        }

        public void Emit(AgentEvent agentEvent)
        {
            lock (_gate) _events.Add(agentEvent);
        }
    }
}
