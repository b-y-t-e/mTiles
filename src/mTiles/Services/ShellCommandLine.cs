using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Turning a command into the command line that runs it in a given shell and exits —
/// <c>pwsh -Command "…"</c>, <c>cmd /c "…"</c>, <c>bash -c "…"</c>.
/// <para>Here rather than on <see cref="ShellProfile"/> because that is a DTO: it is deserialised from
/// settings and carries no behaviour. One place for the mapping all the same — every caller that wants
/// to run a command would otherwise grow its own copy of the same switch.</para>
/// <para><b><c>cmd.exe</c> is only approximately supported for chain commands, and knowingly so.</b>
/// The arguments produced here are joined into one Windows command line by the PTY backend, which
/// quotes per the MSVCRT / <c>CommandLineToArgvW</c> rules that almost every program parses with — and
/// <c>cmd.exe</c> is the notable one that does not: after <c>/c</c> it applies its own rules, in which
/// a quote is not an escape and <c>&amp; | ^ &lt; &gt; %</c> keep their meaning. A command carrying any
/// of those can therefore reach <c>cmd</c> differently from how it was written. A multi-line one is
/// worse still: <c>cmd /c</c> runs the first line and silently discards the rest (measured).
/// <para>Not worked around, because the workaround is a second quoting implementation whose only job is
/// to disagree with the first. It is <em>avoided</em> instead: a profile whose shell is <c>cmd</c> has
/// its chain commands run by PowerShell or a POSIX shell — see
/// <see cref="ShellDetector.ResolveForCommands(ShellProfile)"/> — so the mapping below is reached with
/// <c>/c</c> only when nothing else is installed. The tile's interactive shell is untouched, which is
/// why <c>/c</c> stays here rather than being deleted.</para></para>
/// </summary>
internal static class ShellCommandLine
{
    /// <summary>
    /// The executable and arguments that run <paramref name="command"/> non-interactively.
    /// <para><see cref="ShellProfile.Args"/> is deliberately left out: those are the interactive-startup
    /// flags (<c>--login -i</c>, <c>-l</c>), and <c>-i</c> together with <c>-c</c> asks for a shell that
    /// is both at once. The cost is that a login profile's PATH is not applied to the command, which is
    /// why the fallback chain ends by starting the shell interactively, with those args.</para>
    /// </summary>
    public static (string Executable, string[] Args) For(ShellProfile shell, string command) =>
        (shell.ExecutablePath, [FlagFor(shell.Type), command]);

    /// <summary>A switch, and honestly one: adding a shell means editing this method. It is contained
    /// to a single line here instead of being spread across the callers, which is the whole claim.</summary>
    private static string FlagFor(ShellType type) => type switch
    {
        ShellType.Cmd => "/c",
        ShellType.PowerShell => "-Command",
        _ => "-c",   // bash, zsh, fish and every other POSIX-style shell
    };
}
