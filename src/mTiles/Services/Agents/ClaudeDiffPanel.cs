using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace mTiles.Services.Agents;

/// <summary>
/// Keeps Claude Code's diff panel closed when a session starts.
/// </summary>
/// <remarks>
/// <para><b>What it is.</b> Claude Code 2.1.280, on its fullscreen renderer (which every tile runs — ADR
/// 0001), opens a panel of the working tree's diff beside the conversation by itself once the window is
/// wide enough and files have changed. In a tile that reads as an old conversation loading next to the
/// current one, with a scrollbar of its own. Measured 2026-09-24 in the binary: the auto-open is gated on
/// <c>diffSidebarOpen</c> in the account's <c>.claude.json</c> — <c>false</c> stops it, and
/// <c>/diff</c> writes the key whenever the panel is toggled.</para>
/// <para><b>Reset at every launch</b>, so the owner's rule — closed by default in every conversation —
/// holds even after <c>/diff</c> opened it in the last one. The panel is still one <c>/diff</c> away.</para>
/// <para>It writes into the CLI's own file, which this application otherwise only reads (and, for the
/// token, <c>ClaudeCredentialStore</c> rewrites). One key, nothing else touched, through a temporary file
/// and a move, and every failure is a line in the log: a launch never stops over a panel.</para>
/// </remarks>
internal static partial class ClaudeDiffPanel
{
    private const string Key = "diffSidebarOpen";

    /// <summary>Sets <c>diffSidebarOpen</c> to false in <paramref name="claudeJson"/> unless it already
    /// is. A missing file is created holding only that key, for a sign-in directory the CLI has not
    /// written yet.</summary>
    public static void KeepClosed(string claudeJson)
    {
        try
        {
            JsonObject document;
            if (File.Exists(claudeJson))
            {
                var text = File.ReadAllText(claudeJson);
                if (AlreadyClosed().IsMatch(text)) return;
                if (JsonNode.Parse(text) is not JsonObject parsed) return;
                document = parsed;
            }
            else
            {
                if (Path.GetDirectoryName(claudeJson) is not { } directory || !Directory.Exists(directory))
                    return;
                document = new JsonObject();
            }

            document[Key] = false;

            var temporary = claudeJson + ".mtiles-tmp";
            PrivateFile.WriteAllText(temporary,
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, claudeJson, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not keep Claude Code's diff panel closed in {0}: {1}", claudeJson, ex.Message);
        }
    }

    [GeneratedRegex("\"diffSidebarOpen\"\\s*:\\s*false")]
    private static partial Regex AlreadyClosed();
}
