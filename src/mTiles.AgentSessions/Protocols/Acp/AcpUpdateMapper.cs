using System.Text;
using System.Text.Json;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Protocols.Acp;

/// <summary>
/// Turns ACP <c>session/update</c> notifications into the shared events.
/// </summary>
/// <remarks>
/// <para>ACP (Agent Client Protocol, schema 0.11) is the one protocol more than one agent speaks, which is
/// why this lives beside the transport rather than in an agent's class; what is vendor-specific — Grok's
/// <c>x.ai/*</c> extensions — stays with the vendor.</para>
/// <para><b>Stateful, and one per session.</b> An assistant message in ACP is a run of chunks with no
/// id, ended only by something else happening, so this numbers the runs: a tool call closes the current
/// one and the next chunk starts another. A tool's updates arrive as partial copies of the call and are
/// merged here, and an output that is resent whole on every update (Grok does, per t3code) is reduced to
/// what was appended.</para>
/// <para>Shapes are the ACP schema's, as t3code's generated client reads them: <c>tool_call</c> carries
/// <c>toolCallId</c>, <c>title</c>, <c>kind</c>, <c>status</c>, <c>content[]</c> (text, or
/// <c>{type:"diff",path,oldText,newText}</c>), <c>locations[]</c>, <c>rawInput</c>, <c>rawOutput</c>.
/// </para>
/// </remarks>
public sealed class AcpUpdateMapper
{
    private readonly Dictionary<string, string> _toolOutput = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finishedTools = new(StringComparer.Ordinal);
    private int _segment;
    private bool _segmentOpen;

    /// <summary>The events one update says, stamped with the turn it arrived in.</summary>
    public IReadOnlyList<AgentEvent> Map(JsonElement update, string? turnId)
    {
        var events = new List<AgentEvent>();
        switch (update.Str("sessionUpdate"))
        {
            case "agent_message_chunk" when TextOf(update.Prop("content")) is { Length: > 0 } text:
                if (!_segmentOpen && string.IsNullOrWhiteSpace(text)) break;
                _segmentOpen = true;
                events.Add(new AssistantTextDelta(MessageId(turnId), text));
                break;
            case "agent_thought_chunk" when TextOf(update.Prop("content")) is { Length: > 0 } thought:
                events.Add(new ReasoningDelta($"thought-{turnId}-{_segment}", thought));
                break;
            case "tool_call":
                CloseSegment();
                events.AddRange(ToolCall(update, isUpdate: false));
                break;
            case "tool_call_update":
                events.AddRange(ToolCall(update, isUpdate: true));
                break;
            case "plan":
                events.Add(new PlanUpdated(null,
                [
                    .. update.Items("entries").Select(entry => new PlanStep(
                        entry.Str("content") ?? "",
                        entry.Str("status") switch
                        {
                            "in_progress" => PlanStepStatus.InProgress,
                            "completed" => PlanStepStatus.Completed,
                            _ => PlanStepStatus.Pending,
                        })),
                ]));
                break;
            // current_mode_update names the agent's own mode id, which is not one a viewer can offer back —
            // SessionConfigured.Mode is the canonical id — so it is not reported.
            case "usage_update":
                events.Add(new UsageUpdated(new TokenUsage(update.Long("used"), update.Long("size"),
                    CostUsd: update.Prop("cost") is { } cost && cost.Str("currency") is null or "USD"
                        ? ((JsonElement?)cost).Decimal("amount")
                        : null)));
                break;
        }

        return [.. events.Select(e => e with { TurnId = turnId })];
    }

    /// <summary>Ends the current assistant message, so the next chunk starts a new one.</summary>
    public void CloseSegment()
    {
        if (!_segmentOpen) return;
        _segmentOpen = false;
        _segment++;
    }

    /// <summary>Whether a tool call has already been reported finished.</summary>
    public bool IsFinished(string toolCallId) => _finishedTools.Contains(toolCallId);

    public static ToolKind KindOf(string? acpKind) => acpKind switch
    {
        "execute" => ToolKind.Command,
        "read" => ToolKind.FileRead,
        "edit" or "delete" or "move" => ToolKind.FileChange,
        "search" => ToolKind.Search,
        "fetch" => ToolKind.WebFetch,
        _ => ToolKind.Other,
    };

    /// <summary>The detail of an ACP tool call: its command, paths, diff and raw input.</summary>
    public static ToolDetail DetailOf(JsonElement call)
    {
        var rawInput = call.Prop("rawInput");
        var command = rawInput.Str("command") ?? rawInput.Str("CommandLine") ?? rawInput.Str("commandLine")
            ?? rawInput.Str("command_line") ?? rawInput.Str("cmd");
        var paths = call.Items("locations").Select(l => l.Str("path")).OfType<string>().Distinct().ToList();

        var diffs = new StringBuilder();
        foreach (var content in call.Items("content"))
        {
            if (content.Str("type") != "diff" || content.Str("path") is not { } path) continue;
            diffs.Append(SimpleDiff.Unified(path, content.Str("oldText"), content.Str("newText") ?? ""));
            if (!paths.Contains(path)) paths.Add(path);
        }

        return new ToolDetail(
            command,
            paths.Count > 0 ? paths : null,
            rawInput.Str("query") ?? rawInput.Str("pattern") ?? rawInput.Str("url"),
            diffs.Length > 0 ? diffs.ToString() : null,
            command is null ? rawInput.Pretty() : null,
            (int?)(call.Prop("rawOutput").Long("exitCode") ?? call.Prop("rawOutput").Long("exit_code")));
    }

    private IEnumerable<AgentEvent> ToolCall(JsonElement call, bool isUpdate)
    {
        if (call.Str("toolCallId") is not { } id) yield break;

        var status = call.Str("status");
        var detail = DetailOf(call);
        var output = OutputOf(call);

        if (!isUpdate)
            yield return new ToolStarted(id, KindOf(call.Str("kind")), call.Str("kind") ?? "tool",
                call.Str("title") ?? "Tool", detail);
        else if (!_finishedTools.Contains(id))
            yield return new ToolUpdated(id, call.Str("title"), detail, OutputDelta(id, output));

        if (status is "completed" or "failed" && _finishedTools.Add(id))
            yield return new ToolCompleted(id, status == "failed" ? ToolStatus.Failed : ToolStatus.Completed,
                output ?? _toolOutput.GetValueOrDefault(id));
    }

    /// <summary>What is new in an output that may have been resent whole.</summary>
    private string? OutputDelta(string id, string? output)
    {
        if (output is null) return null;
        var previous = _toolOutput.GetValueOrDefault(id, "");
        _toolOutput[id] = output;
        if (output.StartsWith(previous, StringComparison.Ordinal))
            return output.Length > previous.Length ? output[previous.Length..] : null;
        return null;
    }

    private static string? OutputOf(JsonElement call)
    {
        var text = new StringBuilder();
        foreach (var content in call.Items("content"))
            if (content.Str("type") == "content" && TextOf(content.Prop("content")) is { } part)
                text.Append(part);

        if (text.Length > 0) return text.ToString();
        var raw = call.Prop("rawOutput");
        return raw.Str("output") ?? raw.Str("combinedOutput") ?? raw.Str("combined_output") ?? raw.Str("stdout");
    }

    private static string? TextOf(JsonElement? block) =>
        block.Str("type") == "text" ? block.Str("text") : null;

    private string MessageId(string? turnId) => $"assistant-{turnId}-{_segment}";
}
