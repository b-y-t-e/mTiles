using System.Text.Json;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Protocols;

namespace mTiles.Services.Agents.Sessions.Antigravity;

/// <summary>
/// agy's tools, by the names its <c>init</c> lists (agy 1.1.26) and the parameter names measured on
/// them — <c>run_command</c> takes <c>CommandLine</c>, <c>view_file</c> <c>AbsolutePath</c>,
/// <c>find_by_name</c> <c>Pattern</c> and <c>SearchDirectory</c>.
/// </summary>
public static class AntigravityTools
{
    public static ToolKind KindOf(string tool) => tool switch
    {
        "run_command" or "send_command_input" or "command_status" => ToolKind.Command,
        "view_file" or "list_dir" or "read_resource" => ToolKind.FileRead,
        "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "sed_file" or "notebook_edit"
            => ToolKind.FileChange,
        "grep_search" or "find_by_name" => ToolKind.Search,
        "read_url_content" or "search_web" => ToolKind.WebFetch,
        "invoke_subagent" or "browser_subagent" or "define_subagent" => ToolKind.SubAgent,
        "call_mcp_tool" => ToolKind.Mcp,
        _ => ToolKind.Other,
    };

    public static string TitleOf(string tool, JsonElement? parameters)
    {
        var path = PathOf(parameters);
        return tool switch
        {
            "run_command" => parameters.Str("CommandLine") ?? "Run a command",
            "view_file" when path is not null => $"Read {ToolPath.FileName(path)}",
            "list_dir" when path is not null => $"List {path}",
            "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "sed_file" when path is not null
                => $"Edit {ToolPath.FileName(path)}",
            "grep_search" or "find_by_name" => $"Search {parameters.Str("Query") ?? parameters.Str("Pattern")}".TrimEnd(),
            "read_url_content" => $"Fetch {parameters.Str("Url")}".TrimEnd(),
            "search_web" => $"Search the web {parameters.Str("query") ?? parameters.Str("Query")}".TrimEnd(),
            _ => tool.Replace('_', ' '),
        };
    }

    public static ToolDetail DetailOf(string tool, JsonElement? parameters)
    {
        var path = PathOf(parameters);
        return KindOf(tool) switch
        {
            ToolKind.Command => new ToolDetail(Command: parameters.Str("CommandLine")),
            ToolKind.FileRead or ToolKind.FileChange => new ToolDetail(Paths: path is null ? null : [path],
                Input: tool is "view_file" or "list_dir" ? null : parameters.Pretty()),
            ToolKind.Search => new ToolDetail(Paths: path is null ? null : [path],
                Query: parameters.Str("Query") ?? parameters.Str("Pattern")),
            ToolKind.WebFetch => new ToolDetail(Query: parameters.Str("Url") ?? parameters.Str("query")),
            _ => new ToolDetail(Input: parameters.Pretty()),
        };
    }

    private static string? PathOf(JsonElement? parameters) =>
        parameters.Str("AbsolutePath") ?? parameters.Str("TargetFile") ?? parameters.Str("DirectoryPath")
        ?? parameters.Str("SearchDirectory") ?? parameters.Str("SearchPath") ?? parameters.Str("Path");
}
