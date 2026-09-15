using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Claude;

/// <summary>
/// What Claude Code's tools are, read off their names and inputs.
/// </summary>
/// <remarks>
/// <para>Claude Code's tool names and input fields are its own contract — <c>Bash</c> with
/// <c>command</c>, <c>Edit</c> with <c>file_path</c>, <c>old_string</c>, <c>new_string</c> — and they are
/// read here and nowhere else. The tool list is the one <c>system/init</c> reported on 2.1.272.</para>
/// <para>An unknown name is <see cref="ToolKind.Other"/> with its raw input shown, never an error: tools
/// arrive with MCP servers and plugins, and there is no closed list of them.</para>
/// </remarks>
public static class ClaudeTools
{
    public static ToolKind KindOf(string name) => name switch
    {
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" or "Monitor" => ToolKind.Command,
        "Read" or "NotebookRead" or "LS" => ToolKind.FileRead,
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => ToolKind.FileChange,
        "Glob" or "Grep" or "ToolSearch" => ToolKind.Search,
        "WebFetch" or "WebSearch" => ToolKind.WebFetch,
        "Task" or "Agent" or "Workflow" => ToolKind.SubAgent,
        _ when name.StartsWith("mcp__", StringComparison.Ordinal) => ToolKind.Mcp,
        _ => ToolKind.Other,
    };

    /// <summary>One line saying what the call does, from its input where the input is known.</summary>
    public static string TitleOf(string name, JsonElement? input)
    {
        var subject = name switch
        {
            "Bash" or "PowerShell" => input.Str("description") ?? input.Str("command"),
            "Read" or "Write" or "Edit" or "MultiEdit" => FileName(input.Str("file_path")),
            "NotebookEdit" => FileName(input.Str("notebook_path")),
            "Glob" or "Grep" => input.Str("pattern"),
            "WebFetch" => input.Str("url"),
            "WebSearch" => input.Str("query"),
            "Task" or "Agent" => input.Str("description"),
            "Skill" => input.Str("skill") ?? input.Str("command"),
            "TodoWrite" => "Updating the plan",
            _ => null,
        };

        var verb = name switch
        {
            "Read" => "Read",
            "Write" => "Write",
            "Edit" or "MultiEdit" => "Edit",
            "Glob" => "Find",
            "Grep" => "Search",
            "WebFetch" => "Fetch",
            "WebSearch" => "Search the web",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) => name["mcp__".Length..].Replace("__", ": "),
            _ => name,
        };

        return subject is { Length: > 0 } ? $"{verb} {Flatten(subject)}" : verb;
    }

    public static ToolDetail DetailOf(string name, JsonElement input)
    {
        var path = input.Str("file_path") ?? input.Str("notebook_path") ?? input.Str("path");
        return name switch
        {
            "Bash" or "PowerShell" => new ToolDetail(Command: input.Str("command")),
            "Read" => new ToolDetail(Paths: Paths(path)),
            "Write" when path is not null => new ToolDetail(Paths: Paths(path),
                Diff: SimpleDiff.Unified(Relative(path), null, input.Str("content") ?? "")),
            "Edit" when path is not null => new ToolDetail(Paths: Paths(path),
                Diff: SimpleDiff.Unified(Relative(path), input.Str("old_string") ?? "", input.Str("new_string") ?? "")),
            "MultiEdit" when path is not null => new ToolDetail(Paths: Paths(path),
                Diff: string.Concat(input.Items("edits").Select(edit =>
                    SimpleDiff.Unified(Relative(path), edit.Str("old_string") ?? "", edit.Str("new_string") ?? "")))),
            "Glob" or "Grep" => new ToolDetail(Paths: Paths(path), Query: input.Str("pattern")),
            "WebFetch" => new ToolDetail(Query: input.Str("url")),
            "WebSearch" => new ToolDetail(Query: input.Str("query")),
            _ => new ToolDetail(Paths: Paths(path), Input: ((JsonElement?)input).Pretty()),
        };
    }

    /// <summary>A <c>TodoWrite</c> call as the agent's plan.</summary>
    public static PlanUpdated PlanOf(JsonElement input) =>
        new(null,
        [
            .. input.Items("todos").Select(todo => new PlanStep(
                todo.Str("content") ?? todo.Str("activeForm") ?? "",
                todo.Str("status") switch
                {
                    "in_progress" => PlanStepStatus.InProgress,
                    "completed" => PlanStepStatus.Completed,
                    _ => PlanStepStatus.Pending,
                })),
        ]);

    public static ApprovalKind ApprovalKindOf(string name) => KindOf(name) switch
    {
        ToolKind.Command => ApprovalKind.Command,
        ToolKind.FileChange => ApprovalKind.FileChange,
        ToolKind.FileRead => ApprovalKind.FileRead,
        _ => ApprovalKind.Other,
    };

    /// <summary>
    /// Whether a failed tool result is the harness saying the call was not allowed.
    /// </summary>
    /// <remarks>Two narrow phrases rather than the word "permission", because a <c>git push</c>'s
    /// <c>Permission denied (publickey)</c> is an ordinary failure of the work — see
    /// <c>ClaudeAgent</c>, which reads its headless runs by the same rule.</remarks>
    public static bool IsPermissionDenial(string resultText) =>
        resultText.Contains("requested permissions", StringComparison.OrdinalIgnoreCase)
        || resultText.Contains("permission to use", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string>? Paths(string? path) => path is null ? null : [path];

    private static string? FileName(string? path) => path is null ? null : Path.GetFileName(path);

    /// <summary>A path as a diff header shows it — the name alone is enough to read.</summary>
    private static string Relative(string path) => path.Replace('\\', '/');

    private static string Flatten(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 80 ? flat : flat[..79] + "…";
    }
}
