using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.OpenCode;

/// <summary>
/// Turns opencode's server-sent events into the shared events.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against <c>opencode serve</c> 1.18.18.</b> A reply is a message made of
/// <em>parts</em>, each re-sent whole by <c>message.part.updated</c> as it changes: <c>text</c> and
/// <c>reasoning</c> (whose typing arrives separately as <c>message.part.delta</c> with a
/// <c>partID</c>), <c>tool</c> with a <c>state</c> going <c>pending → running → completed | error</c>,
/// and <c>step-start</c>/<c>step-finish</c>, the second carrying the step's tokens and cost.</para>
/// <para><b>The user's own message comes back too</b>, as a text part of a message whose
/// <c>message.updated</c> says <c>role: user</c> — so roles are remembered by message id and those parts
/// are skipped: the host already drew what was sent.</para>
/// <para>Stateful and one per session: which part is which kind, and which message is whose.</para>
/// </remarks>
public sealed class OpenCodeEventMapper(string sessionId)
{
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _partKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedTools = new(StringComparer.Ordinal);

    /// <summary>Whether an event is about this session at all.</summary>
    public bool Concerns(JsonElement properties) =>
        (properties.Str("sessionID") ?? properties.Prop("info").Str("sessionID") ?? properties.Prop("part").Str("sessionID"))
        is not { } id || id == sessionId;

    public IReadOnlyList<AgentEvent> Map(string type, JsonElement properties, string? turnId)
    {
        var events = new List<AgentEvent>();
        switch (type)
        {
            // A message's own error is not repeated here: the same failure arrives as session.error, which
            // ends the turn with it — measured, both carry the one sentence, and drawn twice it reads as two.
            case "message.updated" when properties.Prop("info") is { } info && info.Str("id") is { } messageId:
                _roles[messageId] = info.Str("role") ?? "assistant";
                break;
            case "message.part.updated" when properties.Prop("part") is { } part:
                events.AddRange(Part(part));
                break;
            case "message.part.delta" when properties.Str("partID") is { } partId
                                           && properties.Str("delta") is { Length: > 0 } delta
                                           && !IsUsers(properties.Str("messageID")):
                events.Add(_partKinds.GetValueOrDefault(partId) == "reasoning"
                    ? new ReasoningDelta(partId, delta)
                    : new AssistantTextDelta(partId, delta));
                break;
            case "todo.updated":
                events.Add(new PlanUpdated(null,
                [
                    .. properties.Items("todos")
                        .Where(todo => todo.Str("status") != "cancelled")
                        .Select(todo => new PlanStep(todo.Str("content") ?? "", todo.Str("status") switch
                        {
                            "in_progress" => PlanStepStatus.InProgress,
                            "completed" => PlanStepStatus.Completed,
                            _ => PlanStepStatus.Pending,
                        })),
                ]));
                break;
            case "session.compacted":
                events.Add(new NoticeRaised(NoticeLevel.Info, "The conversation was compacted to fit the context."));
                break;
            case "session.status" when properties.Prop("status") is { } status && status.Str("type") == "retry":
                events.Add(new NoticeRaised(NoticeLevel.Warning,
                    $"{status.Str("message") ?? "The request failed"} Retrying…"));
                break;
        }

        return [.. events.Select(e => e with { TurnId = turnId })];
    }

    private IEnumerable<AgentEvent> Part(JsonElement part)
    {
        if (part.Str("id") is not { } id) yield break;
        var kind = part.Str("type") ?? "";
        _partKinds[id] = kind;
        if (IsUsers(part.Str("messageID"))) yield break;

        switch (kind)
        {
            case "text" when part.Str("text") is { Length: > 0 } text && part.Prop("time").Prop("end") is not null:
                yield return new AssistantMessageCompleted(id, text);
                break;
            case "tool" when part.Str("callID") is { } callId && part.Prop("state") is { } state:
                var tool = part.Str("tool") ?? "tool";
                var input = state.Prop("input");
                var detail = OpenCodeTools.DetailOf(tool, input, state.Prop("metadata"));
                if (_startedTools.Add(callId))
                    yield return new ToolStarted(callId, OpenCodeTools.KindOf(tool), tool,
                        OpenCodeTools.TitleOf(tool, input, state.Str("title")), detail);

                switch (state.Str("status"))
                {
                    case "running":
                        yield return new ToolUpdated(callId, OpenCodeTools.TitleOf(tool, input, state.Str("title")), detail);
                        break;
                    case "completed":
                        yield return new ToolCompleted(callId, ToolStatus.Completed, state.Str("output"), detail);
                        break;
                    case "error":
                        yield return new ToolCompleted(callId,
                            OpenCodeTools.IsRejection(state.Str("error")) ? ToolStatus.Declined : ToolStatus.Failed,
                            state.Str("error"), detail);
                        break;
                }

                break;
            case "step-finish" when part.Prop("tokens") is { } tokens:
                yield return new UsageUpdated(new TokenUsage(
                    tokens.Long("total") ?? (tokens.Long("input") ?? 0) + (tokens.Long("output") ?? 0)
                    + (tokens.Prop("cache").Long("read") ?? 0),
                    null,
                    tokens.Long("input"),
                    tokens.Long("output"),
                    ((JsonElement?)part).Decimal("cost")));
                break;
        }
    }

    private bool IsUsers(string? messageId) =>
        messageId is not null && _roles.TryGetValue(messageId, out var role) && role == "user";
}
