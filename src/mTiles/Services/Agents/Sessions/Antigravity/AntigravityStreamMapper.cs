using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Antigravity;

/// <summary>
/// Turns agy's stream-json output into the shared events.
/// </summary>
/// <remarks>
/// <para><b>Measured 2026-09-15 against agy 1.1.26.</b> Everything is an <c>{"event":…}</c> line:
/// <c>init</c> with the <c>conversation_id</c> and <c>permission_mode</c>; <c>step_update</c> per step of
/// the agent's trajectory, a tool step going <c>ACTIVE</c> and then <c>DONE</c> or <c>ERROR</c> with its
/// <c>tool_info</c> (<c>name</c>, <c>parameters</c>, <c>output</c>, <c>error</c>), an
/// <c>agent_response</c> step with its token usage; and one <c>result</c> per turn carrying the reply's
/// text as <c>response</c>.</para>
/// <para><b>Coarser than the other agents, and that is agy's, not ours:</b> the reply is not streamed —
/// an <c>agent_response</c> step carries no text — so it appears whole when the turn ends. Tools are
/// real rows with their inputs and outputs.</para>
/// </remarks>
public sealed class AntigravityStreamMapper
{
    private int _turn;

    public void BeginTurn() => _turn++;

    public IReadOnlyList<AgentEvent> Map(JsonElement line, string? turnId)
    {
        var events = new List<AgentEvent>();
        switch (line.Str("event"))
        {
            case "init":
                events.Add(new SessionConfigured(null, line.Prop("init").Str("permission_mode"), line.Str("conversation_id")));
                break;
            case "step_update" when line.Prop("step_update") is { } step:
                events.AddRange(Step(step));
                break;
            case "result" when line.Prop("result") is { } result:
                if (result.Str("response") is { Length: > 0 } response)
                    events.Add(new AssistantMessageCompleted($"agy-{result.Str("conversation_id")}-{_turn}", response));
                foreach (var denied in result.Items("denied_actions"))
                    events.Add(new NoticeRaised(NoticeLevel.Warning,
                        $"agy was not allowed to {denied.Str("display_name") ?? denied.Str("action")} — headless mode cannot ask. " +
                        "Give the instance a mode that allows it, or an allow rule in agy's settings."));
                break;
        }

        return [.. events.Select(e => e with { TurnId = turnId })];
    }

    /// <summary>How a <c>result</c> line ended the turn.</summary>
    public static (TurnOutcome Outcome, string? Error) OutcomeOf(JsonElement line)
    {
        var result = line.Prop("result");
        return result.Str("status") == "ERROR"
            ? (TurnOutcome.Failed, result.Str("error") ?? "agy reported an error.")
            : (TurnOutcome.Completed, null);
    }

    private static IEnumerable<AgentEvent> Step(JsonElement step)
    {
        var id = $"{step.Str("conversation_id")}-{step.Long("step_index")}";
        switch (step.Str("step_type"))
        {
            case "tool":
                var info = step.Prop("tool_info");
                var name = step.Str("tool_name") ?? info.Str("name") ?? "tool";
                var parameters = info.Prop("parameters");
                var detail = AntigravityTools.DetailOf(name, parameters);
                yield return new ToolStarted(id, AntigravityTools.KindOf(name), name, AntigravityTools.TitleOf(name, parameters), detail);

                switch (step.Str("state"))
                {
                    case "DONE":
                        yield return new ToolCompleted(id, ToolStatus.Completed, info.Str("output"));
                        break;
                    case "ERROR":
                        var error = info.Prop("error").Str("message");
                        yield return new ToolCompleted(id,
                            error?.Contains("denied permission", StringComparison.OrdinalIgnoreCase) == true
                                ? ToolStatus.Declined
                                : ToolStatus.Failed,
                            error);
                        break;
                }

                break;
            case "agent_response" when step.Prop("usage") is { } usage:
                yield return new UsageUpdated(new TokenUsage(usage.Long("total_tokens"), null,
                    usage.Long("input_tokens"), usage.Long("output_tokens")));
                break;
        }
    }
}
