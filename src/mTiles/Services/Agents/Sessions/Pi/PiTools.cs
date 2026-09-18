using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Pi;

/// <summary>
/// pi's built-in tools: <c>read</c>, <c>bash</c>, <c>powershell</c>, <c>edit</c>, <c>write</c>,
/// <c>grep</c>, <c>find</c>, <c>ls</c> — names and argument shapes from pi 0.84.4's <c>core/tools</c>.
/// </summary>
public static class PiTools
{
    public static ToolKind KindOf(string tool) => tool switch
    {
        "bash" or "powershell" => ToolKind.Command,
        "read" or "ls" => ToolKind.FileRead,
        "edit" or "write" => ToolKind.FileChange,
        "grep" or "find" => ToolKind.Search,
        _ => ToolKind.Other,
    };

    public static string TitleOf(string tool, JsonElement? args)
    {
        var path = args.Str("path");
        return tool switch
        {
            "bash" or "powershell" => args.Str("command") ?? tool,
            "read" when path is not null => $"Read {ToolPath.FileName(path)}",
            "edit" when path is not null => $"Edit {ToolPath.FileName(path)}",
            "write" when path is not null => $"Write {ToolPath.FileName(path)}",
            "grep" or "find" when args.Str("pattern") is { } pattern => $"Search {pattern}",
            "ls" => $"List {path ?? "."}",
            _ => tool,
        };
    }

    /// <param name="args">The call's arguments, where known.</param>
    /// <param name="details">A finished call's <c>result.details</c>, where known.</param>
    public static ToolDetail DetailOf(string tool, JsonElement? args, JsonElement? details)
    {
        var path = args.Str("path");
        return new ToolDetail(
            Command: tool is "bash" or "powershell" ? args.Str("command") : null,
            Paths: path is null ? null : [path],
            Query: tool is "grep" or "find" ? args.Str("pattern") : null,
            Diff: details.Str("patch") ?? (tool == "write" && path is not null
                ? SimpleDiff.Unified(path.Replace('\\', '/'), null, args.Str("content") ?? "")
                : null),
            Input: tool is "bash" or "powershell" or "read" or "ls" or "grep" or "find" or "edit" or "write"
                ? null
                : args.Pretty());
    }
}
