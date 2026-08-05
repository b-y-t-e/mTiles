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
    public static Task StartAsync(TerminalControl terminal, string workingDirectory,
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

    /// <summary>Splits a script into the lines to type, each ending in a carriage return.</summary>
    internal static IReadOnlyList<string>? BuildStartupInput(string? script, string tileId)
    {
        if (string.IsNullOrWhiteSpace(script))
            return null;

        return [.. script.TrimEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Replace("${tileId}", tileId) + "\r")];
    }
}
