using mTiles.Models;
using mTiles.Services.Shells;

namespace mTiles.Services;

/// <summary>
/// What an <see cref="InstallPlan"/> actually runs in a tile, as opposed to what it shows first.
/// </summary>
/// <remarks>
/// <para><b>Because the printed form was being executed.</b> <see cref="InstallPlan.CommandLine"/> says
/// in its own remarks that its quoting is naive, is for reading, and "is never what runs" — and the one
/// caller that runs a plan was handing exactly that string to a shell. For <c>npm install -g …</c> it
/// happened to be identical, since no part has a space in it; for the Sign in button, whose command is
/// a whole shell line, every part had one, so the tile received the entire command wrapped in quotes.
/// PowerShell echoed it back as a string and bash answered <c>command not found</c>: the directory was
/// made, the row went on saying "not signed in", and nothing anywhere said why.</para>
/// <para><b>How a program is run is the shell's own sentence</b> — <c>IShellTerminal.Invoke</c> —
/// and not "quote every part and join them", which is what this class did first and what PowerShell
/// refuses outright: a quoted first token there is a string, so the line fails at the parser. A plan
/// with no arguments is a command line somebody has already composed for this shell
/// (<c>ShellTerminal.WithEnv</c> writes one), so it is passed through untouched: quoting it would be
/// quoting a command, which is how this went wrong in the first place.</para>
/// </remarks>
public static class InstallCommand
{
    /// <summary>The line to type into a tile running <paramref name="shell"/>.</summary>
    public static string For(InstallPlan plan, IShellTerminal shell) =>
        plan.Arguments.Count == 0
            ? plan.Executable
            : shell.Invoke(plan.Executable, plan.Arguments);
}
