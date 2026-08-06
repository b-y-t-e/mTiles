using Terminal.Avalonia;
using Terminal.Pty;

namespace mTiles.Services;

/// <summary>
/// Starting a shell in a <see cref="TerminalControl"/>, the way this app needs it: one call that
/// replaces whatever session is in the tile and hands the shell its startup script.
/// </summary>
internal static class ShellStarter
{
    /// <summary>
    /// Runs a command in the terminal, replacing whatever session is in it, and types
    /// <paramref name="startupScript"/> into it once the shell is ready to read.
    /// <para>The script is not written by us: a shell that has not opened its stdin yet silently drops
    /// whatever arrives first, and a restart in the meantime would otherwise type our lines into the
    /// next shell. The control owns both of those — we only fill in <c>${tileId}</c>.</para>
    /// </summary>
    /// <returns>The id of the session this started — the only reliable way to recognise it later.</returns>
    public static Task<int> StartAsync(TerminalControl terminal, string workingDirectory,
        string executable, IReadOnlyList<string> args, string? startupScript = null, string tileId = "",
        CancellationToken cancellationToken = default)
        => terminal.RestartAsync(
            new PtyOptions
            {
                Command = executable,
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            },
            BuildStartupInput(startupScript, tileId),
            cancellationToken);

    /// <summary>
    /// A script, as the lines to type into a live prompt.
    /// <para>Two steps, deliberately separate: what a placeholder means is <see cref="TileScript"/>'s
    /// business, and how a script becomes keystrokes is the terminal's. Doing both in one expression
    /// also resolved the token once per line, so a token spanning nothing in particular still cost a
    /// validation per line — and it is what stands between <see cref="SplitIntoLines"/> and moving into
    /// the control, which is where simulating a keyboard belongs.</para>
    /// </summary>
    internal static IReadOnlyList<string>? BuildStartupInput(string? script, string tileId)
    {
        if (string.IsNullOrWhiteSpace(script))
            return null;

        return SplitIntoLines(TileScript.Resolve(script, tileId));
    }

    /// <summary>One line per command, each submitted with a carriage return. A script edited on Windows
    /// carries CRLF, and a doubled CR would submit an extra empty line.</summary>
    internal static IReadOnlyList<string> SplitIntoLines(string script) =>
        [.. script.TrimEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r') + "\r")];
}
