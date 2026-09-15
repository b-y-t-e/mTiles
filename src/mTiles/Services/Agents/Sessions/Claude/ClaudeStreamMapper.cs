using System.Text;
using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Claude;

/// <summary>
/// Turns Claude Code's stream-json output into the shared events.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against Claude Code 2.1.272</b>, driven the way the Agent SDK drives it
/// (<c>-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages
/// --permission-prompt-tool stdio</c>) with a recording kept in the tests. What arrives, in order:
/// <c>control_response</c> to our <c>initialize</c>, <c>system/init</c> carrying the session id,
/// <c>stream_event</c>s wrapping the Anthropic stream (<c>message_start</c>,
/// <c>content_block_start</c> of <c>text</c>/<c>thinking</c>/<c>tool_use</c>, deltas, stops), a whole
/// <c>assistant</c> message per content block, <c>user</c> messages carrying <c>tool_result</c>s, and
/// one <c>result</c> per turn.</para>
/// <para><b>Both the stream and the whole messages are read</b>, and they do not double: a streamed block
/// and the whole message that repeats it share an id, a delta appends and a completion replaces.
/// Reading only the stream loses a block when partial messages are off in somebody's extra arguments;
/// reading only the whole messages loses the typing.</para>
/// <para>Messages from a sub-agent (<c>parent_tool_use_id</c> set) are not drawn: they are the inside of
/// one <c>Task</c> tool call, whose result says what came of it — the rule t3code follows too.</para>
/// </remarks>
public sealed class ClaudeStreamMapper
{
    private readonly Dictionary<int, BlockState> _blocks = new();
    private readonly Dictionary<string, JsonElement> _toolInputs = new(StringComparer.Ordinal);
    private string _messageId = "message";
    private long? _lastContextTokens;

    /// <summary>The session id the CLI reported, once it has.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The input a tool was called with, as far as it is known — for the permission request
    /// that names the call by id.</summary>
    public JsonElement? InputOf(string toolUseId) =>
        _toolInputs.TryGetValue(toolUseId, out var input) ? input : null;

    /// <summary>The events one line of output says.</summary>
    public IReadOnlyList<AgentEvent> Map(JsonElement line, string? turnId)
    {
        var events = new List<AgentEvent>();
        if (line.Str("parent_tool_use_id") is not null) return events;

        switch (line.Str("type"))
        {
            case "system":
                MapSystem(line, events);
                break;
            case "stream_event" when line.Prop("event") is { } streamEvent:
                MapStreamEvent(streamEvent, events);
                break;
            case "assistant" when line.Prop("message") is { } message:
                MapAssistant(message, events);
                break;
            case "user" when line.Prop("message") is { } message:
                MapToolResults(message, events);
                break;
            case "result":
                MapResult(line, events);
                break;
        }

        return [.. events.Select(e => e with { TurnId = turnId })];
    }

    /// <summary>How a turn's <c>result</c> line ended the turn.</summary>
    public static (TurnOutcome Outcome, string? Error) OutcomeOf(JsonElement result)
    {
        var subtype = result.Str("subtype") ?? "success";
        var reason = result.Str("terminal_reason") ?? "";
        if (reason.StartsWith("aborted", StringComparison.Ordinal)) return (TurnOutcome.Interrupted, null);
        if (subtype == "success" && result.Prop("is_error") is not { ValueKind: JsonValueKind.True })
            return (TurnOutcome.Completed, null);

        var errors = string.Join("\n", result.Items("errors")
            .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
        if (errors.Contains("interrupt", StringComparison.OrdinalIgnoreCase)) return (TurnOutcome.Interrupted, null);
        var text = errors.Length > 0 ? errors : result.Str("result") ?? subtype.Replace('_', ' ');
        return (TurnOutcome.Failed, text);
    }

    private void MapSystem(JsonElement line, List<AgentEvent> events)
    {
        switch (line.Str("subtype"))
        {
            case "init":
                SessionId = line.Str("session_id") ?? SessionId;
                events.Add(new SessionConfigured(line.Str("model"), line.Str("permissionMode"), SessionId));
                break;
            case "compact_boundary":
                events.Add(new NoticeRaised(NoticeLevel.Info, "The conversation was compacted to fit the context."));
                break;
        }
    }

    private void MapStreamEvent(JsonElement e, List<AgentEvent> events)
    {
        switch (e.Str("type"))
        {
            case "message_start":
                _messageId = e.Prop("message").Str("id") ?? $"message-{Guid.NewGuid():N}";
                _blocks.Clear();
                break;
            case "content_block_start" when e.Long("index") is { } index && e.Prop("content_block") is { } block:
                var state = new BlockState(block.Str("type") ?? "", block.Str("id"), block.Str("name"));
                _blocks[(int)index] = state;
                if (state is { Kind: "tool_use" or "server_tool_use" or "mcp_tool_use", ToolId: { } toolId })
                {
                    var name = state.ToolName ?? "tool";
                    events.Add(new ToolStarted(toolId, ClaudeTools.KindOf(name), name, ClaudeTools.TitleOf(name, null),
                        ToolDetail.Empty));
                }

                break;
            case "content_block_delta" when e.Long("index") is { } index && e.Prop("delta") is { } delta
                                            && _blocks.TryGetValue((int)index, out var open):
                switch (delta.Str("type"))
                {
                    case "text_delta" when delta.Str("text") is { Length: > 0 } text:
                        events.Add(new AssistantTextDelta(BlockId(index), text));
                        break;
                    case "thinking_delta" when delta.Str("thinking") is { Length: > 0 } thinking:
                        events.Add(new ReasoningDelta(BlockId(index), thinking));
                        break;
                    case "input_json_delta" when delta.Str("partial_json") is { } json:
                        open.Json.Append(json);
                        break;
                }

                break;
            case "content_block_stop" when e.Long("index") is { } index
                                           && _blocks.TryGetValue((int)index, out var closed)
                                           && closed is { ToolId: { } id, ToolName: { } toolName }:
                if (TryParse(closed.Json.ToString()) is { } input) events.AddRange(ToolInput(id, toolName, input));
                break;
        }
    }

    private void MapAssistant(JsonElement message, List<AgentEvent> events)
    {
        var id = message.Str("id") ?? _messageId;
        var index = 0;
        foreach (var block in message.Items("content"))
        {
            switch (block.Str("type"))
            {
                case "text" when block.Str("text") is { } text:
                    events.Add(new AssistantMessageCompleted($"{id}-{FindIndex(id, "text", index)}", text));
                    break;
                case "tool_use" or "server_tool_use" or "mcp_tool_use"
                    when block.Str("id") is { } toolId && block.Str("name") is { } name:
                    events.Add(new ToolStarted(toolId, ClaudeTools.KindOf(name), name, ClaudeTools.TitleOf(name, null),
                        ToolDetail.Empty));
                    if (block.Prop("input") is { } input) events.AddRange(ToolInput(toolId, name, input));
                    break;
            }

            index++;
        }

        if (message.Prop("usage") is { } usage)
        {
            var used = (usage.Long("input_tokens") ?? 0) + (usage.Long("cache_creation_input_tokens") ?? 0)
                       + (usage.Long("cache_read_input_tokens") ?? 0) + (usage.Long("output_tokens") ?? 0);
            if (used > 0)
            {
                _lastContextTokens = used;
                events.Add(new UsageUpdated(new TokenUsage(used, null)));
            }
        }
    }

    private static void MapToolResults(JsonElement message, List<AgentEvent> events)
    {
        foreach (var block in message.Items("content"))
        {
            if (block.Str("type") != "tool_result" || block.Str("tool_use_id") is not { } id) continue;

            var output = ResultText(block.Prop("content"));
            var failed = block.Prop("is_error") is { ValueKind: JsonValueKind.True };
            var declined = failed && ClaudeTools.IsPermissionDenial(output);
            events.Add(new ToolCompleted(id,
                declined ? ToolStatus.Declined : failed ? ToolStatus.Failed : ToolStatus.Completed, output));
        }
    }

    private void MapResult(JsonElement result, List<AgentEvent> events)
    {
        long? window = null;
        if (result.Prop("modelUsage") is { ValueKind: JsonValueKind.Object } models)
            foreach (var model in models.EnumerateObject())
                if (((JsonElement?)model.Value).Long("contextWindow") is { } size)
                    window = Math.Max(window ?? 0, size);

        var usage = result.Prop("usage");
        events.Add(new UsageUpdated(new TokenUsage(
            _lastContextTokens,
            window,
            usage.Long("input_tokens") is { } input
                ? input + (usage.Long("cache_read_input_tokens") ?? 0) + (usage.Long("cache_creation_input_tokens") ?? 0)
                : null,
            usage.Long("output_tokens"),
            ((JsonElement?)result).Decimal("total_cost_usd"))));
    }

    private IEnumerable<AgentEvent> ToolInput(string toolId, string name, JsonElement input)
    {
        _toolInputs[toolId] = input.Clone();
        yield return new ToolUpdated(toolId, ClaudeTools.TitleOf(name, input), ClaudeTools.DetailOf(name, input));

        if (name == "TodoWrite") yield return ClaudeTools.PlanOf(input);
    }

    private string BlockId(long index) => $"{_messageId}-{index}";

    /// <summary>
    /// The stream's index for a block of a whole message, so the streamed text and the whole text share
    /// an id.
    /// </summary>
    /// <remarks>Measured: the CLI writes one whole <c>assistant</c> line <em>per content block</em>, each
    /// holding only that block — so a text block the stream numbered 1 arrives as element 0. The first
    /// streamed block of the same kind not yet completed is the one it repeats.</remarks>
    private int FindIndex(string messageId, string kind, int position)
    {
        if (messageId != _messageId) return position;
        foreach (var (index, block) in _blocks.OrderBy(pair => pair.Key))
        {
            if (block.Kind != kind || block.Completed) continue;
            block.Completed = true;
            return index;
        }

        return position;
    }

    private static string ResultText(JsonElement? content)
    {
        if (content is { ValueKind: JsonValueKind.String } text) return text.GetString() ?? "";
        var builder = new StringBuilder();
        if (content is { ValueKind: JsonValueKind.Array } array)
            foreach (var block in array.EnumerateArray())
                if (block.Str("type") == "text") builder.Append(block.Str("text"));
        return builder.ToString();
    }

    private static JsonElement? TryParse(string json)
    {
        if (json.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record BlockState(string Kind, string? ToolId, string? ToolName)
    {
        public StringBuilder Json { get; } = new();
        public bool Completed { get; set; }
    }
}
