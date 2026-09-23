using mTiles.Services.Shells;

namespace mTiles.Services;

/// <summary>
/// What a terminal is sent when files are dropped on it: their paths, quoted the way that shell wants them.
/// </summary>
/// <remarks>
/// <para>Paths rather than a synthesised Ctrl+V: every agent CLI here recognises an image path arriving in
/// one burst of input as an image (Claude Code, codex and opencode attach it), and a path does not depend
/// on a clipboard helper being installed — which on Linux is exactly what cannot be relied on.</para>
/// <para><strong>The quoting is the shell's own</strong> (<see cref="IShellTerminal.Quote"/>) and not
/// Windows Terminal's "double quotes when there is a space in it", which is the same trap
/// <c>AiAgent.Interactive</c> exists to avoid: a <c>"</c>, a <c>$</c> or a backtick inside double quotes is
/// read by bash and by PowerShell, and a path with no space in it but a <c>;</c> or an <c>&amp;</c> in it
/// was typed bare — so a file name somebody else chose became a second command the moment the user pressed
/// Enter. On Linux those names are perfectly legal.</para>
/// <para>The paths are separated by a space and followed by one, so a second drop or the words typed after
/// it do not run into the last path. No Enter: a drop adds to the prompt, it never sends it.</para>
/// <para>Pure, because the rule is an opinion about somebody else's input handling and is argued in a
/// test.</para>
/// </remarks>
public static class DroppedPathText
{
    public static string For(IEnumerable<string> paths, IShellTerminal shell) => Join(paths, shell.Quote);

    /// <summary>The same paths for something that is not a shell — an agent's own prompt.</summary>
    /// <remarks>Nothing there parses a command line, so the shell's quoting would arrive as literal
    /// characters and the agent would be handed <c>'C:\…\shot.png'</c>, which is not a path anything
    /// recognises. The rule is Windows Terminal's — double quotes only where a space would otherwise
    /// split the path — because what reads this is a prompt that separates its arguments by spaces and
    /// nothing else.</remarks>
    public static string ForPrompt(IEnumerable<string> paths) => Join(paths, QuoteOnlyIfSpaced);

    private static string Join(IEnumerable<string> paths, Func<string, string> quote)
    {
        var parts = paths
            .Select(Sanitize)
            .Where(path => path.Length > 0)
            .Select(quote)
            .ToList();
        return parts.Count == 0 ? "" : string.Join(' ', parts) + " ";
    }

    /// <remarks>A <c>"</c> is a legal character in a file name on Linux, so it is escaped rather than
    /// removed: dropping it spelled a path that does not exist, and the agent then silently attached
    /// nothing at all. A name with no whitespace in it needs no quoting and is left exactly as it is.
    /// Only the quote is escaped and never the backslash: every Windows path is made of backslashes, and
    /// doubling them spells a different path again.</remarks>
    private static string QuoteOnlyIfSpaced(string path) =>
        path.Any(char.IsWhiteSpace) ? '"' + path.Replace("\"", "\\\"") + '"' : path;

    /// <summary>
    /// Control characters never reach a terminal from here: a raw 0x03 would be an interrupt for every
    /// process on the console, and no real file name carries one.
    /// </summary>
    private static string Sanitize(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : new string(path.Where(c => !char.IsControl(c)).ToArray());
}
