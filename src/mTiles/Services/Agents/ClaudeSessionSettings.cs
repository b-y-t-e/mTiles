using System.Text.Json;
using System.Text.Json.Nodes;

namespace mTiles.Services.Agents;

/// <summary>
/// The <c>--settings</c> file every Claude Code session this application holds is started with: the
/// <c>Concise</c> output style, and — for an instance that asked for it — the output proxy's
/// <c>PreToolUse</c> hook.
/// </summary>
/// <remarks>
/// <para>A file rather than inline JSON: the argument is typed into a shell, and Windows PowerShell 5.1
/// strips the quotes inside an argument it hands to a native program, so <c>{"outputStyle":"Concise"}</c>
/// would arrive as something the CLI cannot parse. A <c>--settings</c> file is layered over the user's own
/// settings, so everything else in theirs still applies. Same category as <see cref="OpenCodeProviderConfig"/>:
/// derived, rewritten only when it differs, never pruned.</para>
/// <para><b>Two files, not one rewritten per launch.</b> The path is what reaches the command line, and
/// two tiles on two instances — one with the proxy, one without — are launched from the same process
/// within milliseconds of each other. One file rewritten per launch is those two tiles racing over a
/// path they have both already been handed, so whichever launched second decides what the first one
/// runs. A file per variant cannot be raced: the name says what is in it.</para>
/// </remarks>
public static class ClaudeSessionSettings
{
    /// <summary>What the plain file holds.</summary>
    public const string Content = "{ \"outputStyle\": \"Concise\" }\n";

    /// <summary>Where the plain file lives.</summary>
    public static string PathFor() => PathFor(withOutputProxy: false);

    /// <summary>Where the file for this variant lives.</summary>
    public static string PathFor(bool withOutputProxy) =>
        Path.Combine(AppPaths.GetAppDataDirectory(), "claude",
            withOutputProxy ? "session-settings-rtk.json" : "session-settings.json");

    /// <summary>What the file for this variant holds.</summary>
    /// <remarks><para>The hook block is rtk's own, measured against rtk 0.46.0 on 2026-09-22 by
    /// running <c>rtk init --global --hook-only</c> against a sandboxed config directory and reading
    /// what it asked to have added: one <c>PreToolUse</c> entry, matcher <c>Bash</c>, command
    /// <c>rtk hook claude</c>.</para>
    /// <para><b>Composed rather than written out as a string</b> so the two variants cannot disagree
    /// about the part they share — the output style is one property in one place, and a file that
    /// carried the hook and silently lost the style would be a different session from the one beside
    /// it.</para></remarks>
    public static string ContentFor(string? rtkPath)
    {
        if (rtkPath is null) return Content;

        var settings = JsonNode.Parse(Content)!.AsObject();
        settings["hooks"] = new JsonObject
        {
            ["PreToolUse"] = new JsonArray
            {
                new JsonObject
                {
                    ["matcher"] = "Bash",
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = OutputProxy.HookCommandFor(rtkPath, "claude"),
                        },
                    },
                },
            },
        };
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Writes the plain file when it is missing or stale and answers where it is, or null when
    /// it could not be written.</summary>
    public static string? Write() => Write(rtkPath: null);

    /// <summary>Writes the file for this variant when it is missing or stale and answers where it is,
    /// or null when it could not be written.</summary>
    /// <remarks>Fails soft: a tile on the CLI's own default style is better than a tile that did not
    /// start. That applies to the proxy too — a hook file that cannot be written costs the tokens it
    /// would have saved, and must not cost the session.</remarks>
    /// <param name="rtkPath">Where rtk is, for the file carrying its hook; null for the plain one.</param>
    public static string? Write(string? rtkPath)
    {
        var path = PathFor(withOutputProxy: rtkPath is not null);
        var content = ContentFor(rtkPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Could not write Claude Code session settings: {ex.Message}");
            return null;
        }
    }
}
