using System.Text;
using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Pi;

/// <summary>
/// Turns pi's RPC events into the shared events.
/// </summary>
/// <remarks>
/// <para><b>Read from pi 0.84.4's own <c>docs/rpc.md</c> and <c>rpc-types.d.ts</c>.</b> An assistant
/// message streams as <c>message_update</c> events carrying an <c>assistantMessageEvent</c> —
/// <c>text_delta</c>, <c>thinking_delta</c>, <c>toolcall_start</c>/<c>toolcall_end</c>, each with a
/// <c>contentIndex</c> — and <c>message_end</c> carries the whole message, which is authoritative. Tools
/// run as <c>tool_execution_start</c>/<c>_end</c>; an <c>edit</c>'s result carries a unified
/// <c>details.patch</c>. <c>agent_settled</c> is the only "nothing more will run".</para>
/// <para>pi has <b>no approval step of its own</b> — its tools run unasked — which the terminal agent's
/// class already says; an extension can still ask through <c>extension_ui_request</c>, which the session
/// turns into questions.</para>
/// </remarks>
public sealed class PiRpcMapper
{
    private int _message;
    private bool _aborted;

    /// <summary>Whether the run that just ended was stopped rather than finished.</summary>
    public bool WasAborted => _aborted;

    public void BeginTurn() => _aborted = false;

    public IReadOnlyList<AgentEvent> Map(JsonElement e, string? turnId)
    {
        var events = new List<AgentEvent>();
        switch (e.Str("type"))
        {
            case "message_start" when e.Prop("message").Str("role") == "assistant":
                _message++;
                break;
            case "message_update" when e.Prop("assistantMessageEvent") is { } update:
                var index = update.Long("contentIndex") ?? 0;
                switch (update.Str("type"))
                {
                    case "text_delta" when update.Str("delta") is { Length: > 0 } text:
                        events.Add(new AssistantTextDelta(BlockId(index), text));
                        break;
                    case "thinking_delta" when update.Str("delta") is { Length: > 0 } thinking:
                        events.Add(new ReasoningDelta(BlockId(index), thinking));
                        break;
                    case "toolcall_start" when update.Str("id") is { } id:
                        var name = update.Str("toolName") ?? "tool";
                        events.Add(new ToolStarted(id, PiTools.KindOf(name), name, PiTools.TitleOf(name, null), ToolDetail.Empty));
                        break;
                    case "toolcall_end" when update.Prop("toolCall") is { } call && call.Str("id") is { } callId:
                        var tool = call.Str("name") ?? "tool";
                        events.Add(new ToolUpdated(callId, PiTools.TitleOf(tool, call.Prop("arguments")),
                            PiTools.DetailOf(tool, call.Prop("arguments"), null)));
                        break;
                }

                break;
            case "message_end" when e.Prop("message") is { } message && message.Str("role") == "assistant":
                events.AddRange(AssistantEnd(message));
                break;
            case "tool_execution_start" when e.Str("toolCallId") is { } id:
                var started = e.Str("toolName") ?? "tool";
                events.Add(new ToolStarted(id, PiTools.KindOf(started), started, PiTools.TitleOf(started, e.Prop("args")),
                    PiTools.DetailOf(started, e.Prop("args"), null)));
                break;
            case "tool_execution_end" when e.Str("toolCallId") is { } id:
                var finished = e.Str("toolName") ?? "tool";
                var result = e.Prop("result");
                events.Add(new ToolCompleted(id,
                    e.Bool("isError") == true ? ToolStatus.Failed : ToolStatus.Completed,
                    TextOf(result),
                    PiTools.DetailOf(finished, null, result.Prop("details"))));
                break;
            case "compaction_end" when e.Bool("aborted") != true:
                events.Add(new NoticeRaised(NoticeLevel.Info, "The conversation was compacted to fit the context."));
                break;
            case "auto_retry_start":
                events.Add(new NoticeRaised(NoticeLevel.Warning,
                    $"{e.Str("errorMessage") ?? "The request failed."} Retrying ({e.Long("attempt")}/{e.Long("maxAttempts")})…"));
                break;
            case "extension_error":
                events.Add(new NoticeRaised(NoticeLevel.Warning, $"An extension failed: {e.Str("error")}"));
                break;
        }

        return [.. events.Select(x => x with { TurnId = turnId })];
    }

    private IEnumerable<AgentEvent> AssistantEnd(JsonElement message)
    {
        var index = 0;
        foreach (var block in message.Items("content"))
        {
            if (block.Str("type") == "text" && block.Str("text") is { } text)
                yield return new AssistantMessageCompleted(BlockId(index), text);
            index++;
        }

        if (message.Str("stopReason") == "aborted") _aborted = true;
        if (message.Str("stopReason") == "error" && message.Str("errorMessage") is { } error)
            yield return new NoticeRaised(NoticeLevel.Error, error);

        if (message.Prop("usage") is { } usage)
            yield return new UsageUpdated(new TokenUsage(
                usage.Long("totalTokens"),
                null,
                usage.Long("input"),
                usage.Long("output"),
                usage.Prop("cost").Decimal("total")));
    }

    private static string? TextOf(JsonElement? result)
    {
        var text = new StringBuilder();
        foreach (var block in result.Items("content"))
            if (block.Str("type") == "text") text.Append(block.Str("text"));
        return text.Length > 0 ? text.ToString() : null;
    }

    private string BlockId(long index) => $"pi-{_message}-{index}";
}
