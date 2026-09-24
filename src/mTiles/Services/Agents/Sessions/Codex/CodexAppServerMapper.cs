using System.Text;
using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Codex;

/// <summary>
/// Turns codex app-server notifications into the shared events.
/// </summary>
/// <remarks>
/// <para><b>Read against codex-cli 0.153.2's own schema</b> (<c>codex app-server generate-json-schema</c>)
/// and the mapping t3code runs. A turn is a list of <em>items</em> — <c>agentMessage</c>,
/// <c>reasoning</c>, <c>commandExecution</c>, <c>fileChange</c>, <c>mcpToolCall</c>, <c>webSearch</c>,
/// <c>plan</c>, <c>contextCompaction</c> — each started, streamed by its own delta notification, and
/// completed with its final state, so an item's id is the id of the message or tool call it becomes.
/// </para>
/// <para>User messages are not drawn from here: the host recorded what was sent before codex saw it.</para>
/// </remarks>
public static class CodexAppServerMapper
{
    public static IReadOnlyList<AgentEvent> Map(string method, JsonElement parameters, string? turnId)
    {
        var events = new List<AgentEvent>();
        switch (method)
        {
            case "thread/started" when parameters.Prop("thread").Str("id") is { } threadId:
                events.Add(new SessionConfigured(null, null, threadId));
                break;
            case "item/started" when parameters.Prop("item") is { } item:
                if (Started(item) is { } started) events.Add(started);
                break;
            case "item/completed" when parameters.Prop("item") is { } item:
                events.AddRange(Completed(item));
                break;
            case "item/agentMessage/delta" when parameters.Str("itemId") is { } id && parameters.Str("delta") is { } delta:
                events.Add(new AssistantTextDelta(id, delta));
                break;
            case "item/reasoning/textDelta" or "item/reasoning/summaryTextDelta"
                when parameters.Str("itemId") is { } id && parameters.Str("delta") is { } delta:
                events.Add(new ReasoningDelta(id, delta));
                break;
            case "item/commandExecution/outputDelta" or "item/fileChange/outputDelta"
                when parameters.Str("itemId") is { } id && parameters.Str("delta") is { } delta:
                events.Add(new ToolUpdated(id, OutputDelta: delta));
                break;
            case "turn/plan/updated":
                events.Add(new PlanUpdated(parameters.Str("explanation"),
                [
                    .. parameters.Items("plan").Select(step => new PlanStep(step.Str("step") ?? "",
                        step.Str("status") switch
                        {
                            "inProgress" => PlanStepStatus.InProgress,
                            "completed" => PlanStepStatus.Completed,
                            _ => PlanStepStatus.Pending,
                        })),
                ]));
                break;
            case "thread/tokenUsage/updated" when parameters.Prop("tokenUsage") is { } usage:
                var last = usage.Prop("last");
                var total = usage.Prop("total");
                events.Add(new UsageUpdated(new TokenUsage(
                    last.Long("totalTokens"),
                    usage.Long("modelContextWindow"),
                    total.Long("inputTokens"),
                    total.Long("outputTokens"))));
                break;
            case "error" when parameters.Prop("error").Str("message") is { } message:
                events.Add(new NoticeRaised(parameters.Bool("willRetry") == true ? NoticeLevel.Warning : NoticeLevel.Error,
                    parameters.Bool("willRetry") == true ? $"{message} Retrying…" : message));
                break;
        }

        return [.. events.Select(e => e with { TurnId = turnId })];
    }

    /// <summary>How <c>turn/completed</c> ended the turn.</summary>
    public static (TurnOutcome Outcome, string? Error) OutcomeOf(JsonElement parameters)
    {
        var turn = parameters.Prop("turn");
        return turn.Str("status") switch
        {
            "interrupted" => (TurnOutcome.Interrupted, null),
            "failed" => (TurnOutcome.Failed, turn.Prop("error").Str("message") ?? "The turn failed."),
            _ => (TurnOutcome.Completed, null),
        };
    }

    public static ToolDetail DetailOf(JsonElement item) => item.Str("type") switch
    {
        "commandExecution" => new ToolDetail(Command: item.Str("command"), ExitCode: (int?)item.Long("exitCode")),
        "fileChange" => new ToolDetail(
            Paths: [.. item.Items("changes").Select(c => c.Str("path")).OfType<string>()],
            Diff: string.Concat(item.Items("changes").Select(c => DiffOf(c)))),
        "mcpToolCall" => new ToolDetail(Input: item.Prop("arguments").Pretty()),
        "dynamicToolCall" => new ToolDetail(Input: item.Prop("arguments").Pretty()),
        "webSearch" => new ToolDetail(Query: item.Str("query")),
        "imageView" => new ToolDetail(Paths: item.Str("path") is { } path ? [path] : null),
        "collabAgentToolCall" => new ToolDetail(Input: item.Str("prompt")),
        _ => ToolDetail.Empty,
    };

    /// <summary>What a sub-agent is doing, in the words its tool's row would have: the item it has just
    /// started, where that item is a tool call.</summary>
    public static string? ProgressOf(JsonElement item) =>
        Describe(item) is ({ }, _, { Length: > 0 } title) ? title : null;

    private static AgentEvent? Started(JsonElement item)
    {
        if (item.Str("id") is not { } id) return null;
        var (kind, name, title) = Describe(item);
        return kind is { } toolKind ? new ToolStarted(id, toolKind, name, title, DetailOf(item)) : null;
    }

    private static IEnumerable<AgentEvent> Completed(JsonElement item)
    {
        if (item.Str("id") is not { } id) yield break;

        switch (item.Str("type"))
        {
            case "agentMessage":
                yield return new AssistantMessageCompleted(id, item.Str("text") ?? "");
                yield break;
            case "plan" when item.Str("text") is { Length: > 0 } plan:
                yield return new PlanProposed(plan);
                yield break;
            case "contextCompaction":
                yield return new NoticeRaised(NoticeLevel.Info, "The conversation was compacted to fit the context.");
                yield break;
        }

        var (kind, name, title) = Describe(item);
        if (kind is not { } toolKind) yield break;

        // A start that was missed — a resumed thread, a notification dropped — still gets its row.
        yield return new ToolStarted(id, toolKind, name, title, DetailOf(item));
        yield return new ToolCompleted(id,
            item.Str("status") switch
            {
                "failed" => ToolStatus.Failed,
                "declined" => ToolStatus.Declined,
                _ => ToolStatus.Completed,
            },
            OutputOf(item),
            DetailOf(item));
    }

    private static (ToolKind? Kind, string Name, string Title) Describe(JsonElement item) => item.Str("type") switch
    {
        "commandExecution" => (ToolKind.Command, "shell", item.Str("command") ?? "Run a command"),
        "fileChange" => (ToolKind.FileChange, "apply_patch", FileChangeTitle(item)),
        "mcpToolCall" => (ToolKind.Mcp, item.Str("tool") ?? "mcp", $"{item.Str("server")}: {item.Str("tool")}"),
        "dynamicToolCall" => (ToolKind.Other, item.Str("tool") ?? "tool", item.Str("tool") ?? "Tool"),
        "webSearch" => (ToolKind.WebFetch, "web_search", $"Search the web {item.Str("query")}".TrimEnd()),
        "imageView" => (ToolKind.FileRead, "view_image", $"View {ToolPath.FileName(item.Str("path") ?? "")}".TrimEnd()),
        "collabAgentToolCall" => (ToolKind.SubAgent, item.Str("tool") ?? "agent", $"Sub-agent: {item.Str("tool")}"),
        _ => (null, "", ""),
    };

    private static string FileChangeTitle(JsonElement item)
    {
        var paths = item.Items("changes").Select(c => c.Str("path")).OfType<string>().ToList();
        return paths.Count switch
        {
            0 => "Edit files",
            1 => $"Edit {ToolPath.FileName(paths[0])}",
            _ => $"Edit {paths.Count} files",
        };
    }

    private static string DiffOf(JsonElement change)
    {
        var diff = change.Str("diff") ?? "";
        if (diff.StartsWith("---", StringComparison.Ordinal) || diff.StartsWith("diff ", StringComparison.Ordinal))
            return diff.EndsWith('\n') ? diff : diff + "\n";

        // A hunk without its header: give it one so a diff view can place it.
        var path = (change.Str("path") ?? "file").Replace('\\', '/');
        return $"--- a/{path}\n+++ b/{path}\n{diff}{(diff.EndsWith('\n') ? "" : "\n")}";
    }

    private static string? OutputOf(JsonElement item)
    {
        switch (item.Str("type"))
        {
            case "commandExecution":
                return item.Str("aggregatedOutput");
            case "mcpToolCall":
                if (item.Prop("error").Str("message") is { } error) return error;
                var text = new StringBuilder();
                foreach (var block in item.Prop("result").Items("content"))
                    if (block.Str("text") is { } part) text.Append(part);
                return text.Length > 0 ? text.ToString() : null;
            default:
                return null;
        }
    }
}
