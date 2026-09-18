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
    /// <remarks><b>The installer is resolved to a file before the shell is asked to run it</b>, for the
    /// reason <c>IShellTerminal.Program</c> gives: every plan here runs <c>npm</c>, npm on Windows is
    /// <c>npm.ps1</c> as far as PowerShell's own lookup is concerned, and a default Windows refuses to
    /// load a script — so the Install… button failed with an execution-policy error on exactly the
    /// machines that have nothing installed yet. The shell decides whether that file is used at all
    /// (<c>IShellTerminal.Program</c>: only PowerShell takes the path, the rest keep the name).
    /// <c>ExecutableFinder.Anywhere</c> answers with the <c>.exe</c> or the
    /// <c>.cmd</c> and never with a <c>.ps1</c>, and a name it cannot find is passed through as the
    /// name, which is what this always did.</remarks>
    public static string For(InstallPlan plan, IShellTerminal shell) =>
        For(plan, shell, ExecutableFinder.Anywhere);

    /// <summary>As <see cref="For(InstallPlan, IShellTerminal)"/>, with where a binary is found
    /// supplied by the caller — so a test states the machine instead of depending on it.</summary>
    internal static string For(InstallPlan plan, IShellTerminal shell, Func<string, string?> locate) =>
        plan.Arguments.Count == 0
            ? plan.Executable
            : Line(plan.Executable, plan.Arguments, shell, locate);

    /// <summary>The line that runs the program called <paramref name="name"/> with
    /// <paramref name="arguments"/> in <paramref name="shell"/>, the binary resolved to a file by the
    /// same rule an install uses — for a command composed here rather than carried by a plan (the CCS
    /// proxy's login).</summary>
    public static string Line(string name, IReadOnlyList<string> arguments, IShellTerminal shell) =>
        Line(name, arguments, shell, ExecutableFinder.Anywhere);

    private static string Line(string name, IReadOnlyList<string> arguments, IShellTerminal shell,
        Func<string, string?> locate) =>
        string.Join(' ',
            new[] { shell.Program(name, locate(name), arguments) }.Concat(arguments.Select(shell.Quote)));
}
