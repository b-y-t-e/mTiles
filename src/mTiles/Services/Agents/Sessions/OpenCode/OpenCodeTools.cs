using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.OpenCode;

/// <summary>
/// What opencode's built-in tools are, read off their names and inputs.
/// </summary>
/// <remarks>Measured 2026-09-15 (opencode 1.18.18): <c>read</c> takes <c>filePath</c>, and a tool's state
/// carries a <c>title</c> of its own once it runs. The rest follows opencode's tool set as t3code
/// classifies it; an unknown name is <see cref="ToolKind.Other"/> with its input shown.</remarks>
public static class OpenCodeTools
{
    public static ToolKind KindOf(string tool) => tool switch
    {
        "bash" or "shell" => ToolKind.Command,
        "read" or "list" => ToolKind.FileRead,
        // apply_patch is what 1.18.18 used for a new file on an OpenAI model (measured, live run).
        "edit" or "write" or "patch" or "multiedit" or "apply_patch" => ToolKind.FileChange,
        "glob" or "grep" or "codesearch" => ToolKind.Search,
        "webfetch" or "websearch" => ToolKind.WebFetch,
        "task" => ToolKind.SubAgent,
        _ when tool.Contains("mcp", StringComparison.OrdinalIgnoreCase) => ToolKind.Mcp,
        _ => ToolKind.Other,
    };

    public static string TitleOf(string tool, JsonElement? input, string? stateTitle)
    {
        var path = input.Str("filePath") ?? input.Str("path");
        return tool switch
        {
            "bash" => input.Str("description") ?? input.Str("command") ?? stateTitle ?? "Run a command",
            "read" when path is not null => $"Read {ToolPath.FileName(path)}",
            "edit" or "multiedit" or "patch" when path is not null => $"Edit {ToolPath.FileName(path)}",
            "write" when path is not null => $"Write {ToolPath.FileName(path)}",
            "glob" or "grep" when input.Str("pattern") is { } pattern => $"Search {pattern}",
            "webfetch" when input.Str("url") is { } url => $"Fetch {url}",
            "task" when input.Str("description") is { } description => description,
            "todowrite" => "Updating the plan",
            "apply_patch" => stateTitle is { Length: > 0 } ? stateTitle : "Apply a patch",
            _ => stateTitle is { Length: > 0 } ? stateTitle : tool,
        };
    }

    public static ToolDetail DetailOf(string tool, JsonElement? input, JsonElement? metadata)
    {
        var path = input.Str("filePath") ?? input.Str("path");
        var paths = path is null ? null : new[] { path };
        return tool switch
        {
            "bash" => new ToolDetail(Command: input.Str("command"), ExitCode: (int?)metadata.Long("exit")),
            "read" or "list" => new ToolDetail(Paths: paths),
            "edit" when path is not null => new ToolDetail(Paths: paths,
                Diff: metadata.Str("diff") ?? SimpleDiff.Unified(path.Replace('\\', '/'),
                    input.Str("oldString") ?? "", input.Str("newString") ?? "")),
            "write" when path is not null => new ToolDetail(Paths: paths,
                Diff: metadata.Str("diff") ?? SimpleDiff.Unified(path.Replace('\\', '/'), null, input.Str("content") ?? "")),
            "apply_patch" => new ToolDetail(Diff: metadata.Str("diff") ?? input.Str("patchText")),
            "glob" or "grep" => new ToolDetail(Paths: paths, Query: input.Str("pattern")),
            "webfetch" => new ToolDetail(Query: input.Str("url")),
            _ => new ToolDetail(Paths: paths, Input: input is { ValueKind: JsonValueKind.Object } i && i.EnumerateObject().Any()
                ? input.Pretty()
                : null),
        };
    }

    /// <summary>Whether a tool's error is the user having rejected it rather than it failing.</summary>
    public static bool IsRejection(string? error) =>
        error is not null && (error.Contains("rejected permission", StringComparison.OrdinalIgnoreCase)
                              || error.Contains("user rejected", StringComparison.OrdinalIgnoreCase));

    public static ApprovalKind ApprovalKindOf(string? permission) => permission switch
    {
        "bash" => ApprovalKind.Command,
        "edit" or "write" => ApprovalKind.FileChange,
        "read" => ApprovalKind.FileRead,
        _ => ApprovalKind.Other,
    };
}
