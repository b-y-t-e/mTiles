using System.Text.Json;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols.Acp;
using mTiles.Services.Agents.Sessions.Antigravity;
using mTiles.Services.Agents.Sessions.Claude;
using mTiles.Services.Agents.Sessions.Codex;
using mTiles.Services.Agents.Sessions.OpenCode;
using mTiles.Services.Agents.Sessions.Pi;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Every agent's protocol, translated into the one vocabulary.
/// </summary>
/// <remarks>
/// <para>The lines are recordings — trimmed of the fields nothing here reads — made on 2026-09-15 against
/// Claude Code 2.1.272, opencode 1.18.18 and agy 1.1.26, and taken from the published schemas of codex
/// 0.153.2 (<c>generate-json-schema</c>) and pi 0.84.4 (<c>docs/rpc.md</c>). Each is somebody else's
/// contract: when one moves, a test here fails before a tile quietly stops drawing what the agent did.
/// </para>
/// <para>Every test ends by folding the events through the reducer, because what matters is not the
/// events but the conversation they make — and that has to come out the same for every agent.</para>
/// </remarks>
public class AgentProtocolMapperTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static ConversationState Fold(IEnumerable<AgentEvent> events) => ConversationReducer.Replay(events);

    [Fact]
    public void Claude_Code_stream_json_makes_a_tool_row_a_reply_and_usage()
    {
        var mapper = new ClaudeStreamMapper();
        string[] lines =
        [
            """{"type":"system","subtype":"init","session_id":"f5ae","model":"claude-haiku-4-5-20251001","permissionMode":"default"}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"Write","input":{}}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\": \"C:\\\\w\\\\probe.txt\""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":", \"content\": \"hi\"}"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":1},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"C:\\w\\probe.txt","content":"hi"}}],"usage":{"input_tokens":10,"cache_creation_input_tokens":10281,"cache_read_input_tokens":18655,"output_tokens":4}},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File created successfully"}]},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_2"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"o"}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"ok"}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"sub","content":[{"type":"text","text":"a sub-agent's inside"}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.0353,"usage":{"input_tokens":18,"cache_creation_input_tokens":13709,"cache_read_input_tokens":47591,"output_tokens":433},"modelUsage":{"claude-haiku-4-5":{"contextWindow":200000}}}""",
        ];

        var events = lines.SelectMany(line => mapper.Map(Json(line), "turn")).ToList();
        var state = Fold(events);

        Assert.Equal("f5ae", state.ResumeToken);
        var tool = Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(state.Timeline[0]).Items.Single());
        Assert.Equal(("Write probe.txt", ToolKind.FileChange, ToolCallState.Completed), (tool.Title, tool.Kind, tool.State));
        Assert.Contains("+hi", tool.Detail.Diff);
        var reply = Assert.IsType<MessageEntry>(Assert.Single(state.Timeline, e => e is MessageEntry));
        Assert.Equal("ok", reply.Text);
        Assert.Equal(200_000, state.Usage!.ContextWindow);
        Assert.Equal(0.0353m, state.Usage.CostUsd);
        Assert.DoesNotContain(state.Timeline, e => e is MessageEntry { Text: "a sub-agent's inside" });
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":false}""", TurnOutcome.Completed)]
    [InlineData("""{"type":"result","subtype":"error_during_execution","terminal_reason":"aborted_streaming"}""", TurnOutcome.Interrupted)]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":true,"errors":["max turns"]}""", TurnOutcome.Failed)]
    public void Claude_Code_s_result_says_how_the_turn_ended(string line, TurnOutcome expected) =>
        Assert.Equal(expected, ClaudeStreamMapper.OutcomeOf(Json(line)).Outcome);

    /// <summary>
    /// The one line that puts the tile's "Working" down, and the one that looks exactly like it and
    /// must not: the Task tool runs an agent of its own, and its `result` is interleaved with ours.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"result","subtype":"success"}""", true)]
    [InlineData("""{"type":"result","subtype":"success","parent_tool_use_id":null}""", true)]
    [InlineData("""{"type":"result","subtype":"success","parent_tool_use_id":"toolu_1"}""", false)]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""", false)]
    public void Only_this_conversation_s_own_result_ends_the_turn(string line, bool ends) =>
        Assert.Equal(ends, ClaudeStreamMapper.EndsTurn(Json(line)));

    [Fact]
    public void Claude_Code_s_permission_denial_is_a_declined_tool_not_a_failed_one()
    {
        var mapper = new ClaudeStreamMapper();
        var events = mapper.Map(Json(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","is_error":true,"content":"Claude requested permissions to write to x, but you haven't granted it yet."}]}}"""), null);

        Assert.Equal(ToolStatus.Declined, Assert.IsType<ToolCompleted>(Assert.Single(events)).Status);
    }

    [Fact]
    public void Codex_items_become_rows_and_messages()
    {
        (string Method, string Params)[] notifications =
        [
            ("item/started", """{"threadId":"th","turnId":"tu","item":{"type":"commandExecution","id":"c1","command":"dotnet build","status":"inProgress"}}"""),
            ("item/commandExecution/outputDelta", """{"threadId":"th","turnId":"tu","itemId":"c1","delta":"Build "}"""),
            ("item/completed", """{"threadId":"th","turnId":"tu","item":{"type":"commandExecution","id":"c1","command":"dotnet build","status":"failed","exitCode":1,"aggregatedOutput":"Build FAILED"}}"""),
            ("item/started", """{"threadId":"th","turnId":"tu","item":{"type":"fileChange","id":"f1","status":"inProgress","changes":[{"path":"src/a.cs","kind":{"type":"update"},"diff":"@@ -1 +1 @@\n-old\n+new"}]}}"""),
            ("item/completed", """{"threadId":"th","turnId":"tu","item":{"type":"fileChange","id":"f1","status":"completed","changes":[{"path":"src/a.cs","kind":{"type":"update"},"diff":"@@ -1 +1 @@\n-old\n+new"}]}}"""),
            ("item/agentMessage/delta", """{"threadId":"th","turnId":"tu","itemId":"m1","delta":"Fix"}"""),
            ("item/completed", """{"threadId":"th","turnId":"tu","item":{"type":"agentMessage","id":"m1","text":"Fixed the build."}}"""),
            ("turn/plan/updated", """{"threadId":"th","turnId":"tu","plan":[{"step":"Build","status":"completed"},{"step":"Test","status":"inProgress"}]}"""),
            ("thread/tokenUsage/updated", """{"threadId":"th","turnId":"tu","tokenUsage":{"last":{"totalTokens":5000,"inputTokens":1,"cachedInputTokens":0,"outputTokens":1,"reasoningOutputTokens":0},"total":{"totalTokens":9000,"inputTokens":8000,"cachedInputTokens":0,"outputTokens":1000,"reasoningOutputTokens":0},"modelContextWindow":272000}}"""),
        ];

        var state = Fold(notifications.SelectMany(n => CodexAppServerMapper.Map(n.Method, Json(n.Params), "turn")));

        var group = Assert.IsType<WorkGroupEntry>(state.Timeline[0]);
        var command = Assert.IsType<ToolCallItem>(group.Items[0]);
        Assert.Equal((ToolCallState.Failed, "Build FAILED", 1), (command.State, command.Output, command.Detail.ExitCode));
        var edit = Assert.IsType<ToolCallItem>(group.Items[1]);
        Assert.Equal("Edit a.cs", edit.Title);
        Assert.StartsWith("--- a/src/a.cs", edit.Detail.Diff);
        Assert.Equal("Fixed the build.", Assert.IsType<MessageEntry>(state.Timeline[1]).Text);
        Assert.Equal(PlanStepStatus.InProgress, state.Plan!.Steps[1].Status);
        Assert.Equal((5000L, 272000L), (state.Usage!.UsedTokens!.Value, state.Usage.ContextWindow!.Value));
    }

    [Theory]
    [InlineData("""{"turn":{"id":"t","status":"completed"}}""", TurnOutcome.Completed)]
    [InlineData("""{"turn":{"id":"t","status":"interrupted"}}""", TurnOutcome.Interrupted)]
    [InlineData("""{"turn":{"id":"t","status":"failed","error":{"message":"quota"}}}""", TurnOutcome.Failed)]
    public void Codex_turn_completion_says_how_it_ended(string parameters, TurnOutcome expected) =>
        Assert.Equal(expected, CodexAppServerMapper.OutcomeOf(Json(parameters)).Outcome);

    [Fact]
    public void Opencode_parts_become_rows_and_its_echo_of_the_user_is_not_drawn_twice()
    {
        var mapper = new OpenCodeEventMapper("ses_1");
        (string Type, string Properties)[] events =
        [
            ("message.updated", """{"sessionID":"ses_1","info":{"id":"msg_u","role":"user"}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"type":"text","text":"Read a.txt","messageID":"msg_u","id":"prt_u","time":{"start":1,"end":2}}}"""),
            ("message.updated", """{"sessionID":"ses_1","info":{"id":"msg_a","role":"assistant"}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_t","messageID":"msg_a","type":"tool","tool":"read","callID":"call_1","state":{"status":"pending","input":{},"raw":""}}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_t","messageID":"msg_a","type":"tool","tool":"read","callID":"call_1","state":{"status":"running","input":{"filePath":"C:\\w\\a.txt"},"time":{"start":1}}}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_t","messageID":"msg_a","type":"tool","tool":"read","callID":"call_1","state":{"status":"completed","input":{"filePath":"C:\\w\\a.txt"},"output":"1: hello","metadata":{"preview":"hello"}}}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_x","messageID":"msg_a","type":"text","text":""}}"""),
            ("message.part.delta", """{"sessionID":"ses_1","messageID":"msg_a","partID":"prt_x","field":"text","delta":"hel"}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_x","messageID":"msg_a","type":"text","text":"hello","time":{"start":1,"end":2}}}"""),
            ("message.part.updated", """{"sessionID":"ses_1","part":{"id":"prt_f","messageID":"msg_a","type":"step-finish","reason":"tool-calls","tokens":{"total":7386,"input":7289,"output":97,"reasoning":0,"cache":{"write":0,"read":0}},"cost":0}}"""),
        ];

        var state = Fold(events.SelectMany(e => mapper.Map(e.Type, Json(e.Properties), "turn")));

        Assert.DoesNotContain(state.Timeline, e => e is MessageEntry { Text: "Read a.txt" });
        var tool = Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(state.Timeline[0]).Items.Single());
        Assert.Equal(("Read a.txt", ToolCallState.Completed, "1: hello"), (tool.Title, tool.State, tool.Output));
        Assert.Equal("hello", Assert.IsType<MessageEntry>(state.Timeline[1]).Text);
        Assert.Equal(7386, state.Usage!.UsedTokens);
    }

    [Fact]
    public void Opencode_events_of_another_session_are_not_ours()
    {
        var mapper = new OpenCodeEventMapper("ses_1");
        Assert.False(mapper.Concerns(Json("""{"sessionID":"ses_2"}""")));
        Assert.True(mapper.Concerns(Json("""{"part":{"sessionID":"ses_1"}}""")));
    }

    [Fact]
    public void Pi_rpc_events_make_a_tool_row_with_its_patch_and_a_reply()
    {
        var mapper = new PiRpcMapper();
        string[] lines =
        [
            """{"type":"message_start","message":{"role":"assistant","content":[]}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"hmm"}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_start","contentIndex":1,"id":"tc1","toolName":"edit"}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_end","contentIndex":1,"toolCall":{"type":"toolCall","id":"tc1","name":"edit","arguments":{"path":"src/a.ts","edits":[{"oldText":"a","newText":"b"}]}}}}""",
            """{"type":"tool_execution_start","toolCallId":"tc1","toolName":"edit","args":{"path":"src/a.ts"}}""",
            """{"type":"tool_execution_end","toolCallId":"tc1","toolName":"edit","result":{"content":[{"type":"text","text":"Edited"}],"details":{"patch":"--- a/src/a.ts\n+++ b/src/a.ts\n@@ -1 +1 @@\n-a\n+b\n"}},"isError":false}""",
            """{"type":"message_start","message":{"role":"assistant","content":[]}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Do"}}""",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Done."}],"stopReason":"stop","usage":{"input":100,"output":20,"totalTokens":120,"cost":{"total":0.002}}}}""",
        ];

        var state = Fold(lines.SelectMany(line => mapper.Map(Json(line), "turn")));

        var group = Assert.IsType<WorkGroupEntry>(state.Timeline[0]);
        var tool = Assert.IsType<ToolCallItem>(group.Items.Single(i => i is ToolCallItem));
        Assert.Equal(("Edit a.ts", ToolCallState.Completed), (tool.Title, tool.State));
        Assert.Contains("+b", tool.Detail.Diff);
        Assert.Equal("Done.", Assert.IsType<MessageEntry>(state.Timeline[1]).Text);
        Assert.Equal(0.002m, state.Usage!.CostUsd);
        Assert.False(mapper.WasAborted);
    }

    [Fact]
    public void Antigravity_steps_become_rows_and_the_result_is_the_reply()
    {
        var mapper = new AntigravityStreamMapper();
        mapper.BeginTurn();
        string[] lines =
        [
            """{"event":"init","conversation_id":"205f","init":{"cwd":"C:\\w","permission_mode":"request-review"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"205f","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"find_by_name","tool_info":{"name":"find_by_name","parameters":{"Pattern":"*a.txt*","SearchDirectory":"C:\\w"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"205f","step_index":2,"state":"DONE","step_type":"tool","tool_name":"find_by_name","tool_info":{"name":"find_by_name","parameters":{"Pattern":"*a.txt*","SearchDirectory":"C:\\w"},"output":"1 result"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"205f","step_index":4,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"dir"},"error":{"type":"TOOL_ERROR","message":"permission check failed: user denied permission to run command"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"205f","step_index":5,"state":"DONE","step_type":"agent_response","usage":{"input_tokens":13467,"output_tokens":507,"total_tokens":13974}}}""",
            """{"event":"result","result":{"conversation_id":"205f","status":"SUCCESS","response":"Nothing to do.","denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""",
        ];

        var state = Fold(lines.SelectMany(line => mapper.Map(Json(line), "turn")));

        Assert.Equal("205f", state.ResumeToken);
        var group = Assert.IsType<WorkGroupEntry>(state.Timeline[0]);
        Assert.Equal(ToolCallState.Completed, Assert.IsType<ToolCallItem>(group.Items[0]).State);
        Assert.Equal(("dir", ToolCallState.Declined), (Assert.IsType<ToolCallItem>(group.Items[1]).Title, Assert.IsType<ToolCallItem>(group.Items[1]).State));
        Assert.Equal("Nothing to do.", Assert.IsType<MessageEntry>(state.Timeline[1]).Text);
        Assert.Contains(state.Timeline, e => e is NoticeEntry { Level: NoticeLevel.Warning });
        Assert.Equal(TurnOutcome.Completed, AntigravityStreamMapper.OutcomeOf(Json(lines[^1])).Outcome);
    }

    [Fact]
    public void Acp_updates_make_a_tool_with_its_diff_a_plan_and_split_messages()
    {
        var mapper = new AcpUpdateMapper();
        string[] updates =
        [
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Looking"}}""",
            """{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"Edit a.ts","kind":"edit","status":"pending","content":[{"type":"diff","path":"a.ts","oldText":"x\n","newText":"y\n"}],"locations":[{"path":"a.ts"}]}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"work"}}]}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"working"}}]}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"completed"}""",
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Done"}}""",
            """{"sessionUpdate":"plan","entries":[{"content":"Edit","priority":"high","status":"completed"}]}""",
            """{"sessionUpdate":"usage_update","used":5000,"size":131072,"cost":{"amount":0.01,"currency":"USD"}}""",
        ];

        var state = Fold(updates.SelectMany(update => mapper.Map(Json(update), "turn")));

        Assert.Collection(state.Timeline,
            e => Assert.Equal("Looking", Assert.IsType<MessageEntry>(e).Text),
            e =>
            {
                var tool = Assert.IsType<ToolCallItem>(Assert.IsType<WorkGroupEntry>(e).Items.Single());
                Assert.Equal((ToolKind.FileChange, ToolCallState.Completed, "working"), (tool.Kind, tool.State, tool.Output));
                Assert.Contains("+y", tool.Detail.Diff);
            },
            e => Assert.Equal("Done", Assert.IsType<MessageEntry>(e).Text));
        Assert.Equal(PlanStepStatus.Completed, state.Plan!.Steps.Single().Status);
        Assert.Equal(131072, state.Usage!.ContextWindow);
    }
}
