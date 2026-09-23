namespace mTiles.Models;

/// <summary>
/// What an "install this agent" button would run, as something that can be read before it is run.
/// </summary>
/// <param name="Executable">The program to launch — <c>npm</c>, <c>brew</c>, <c>winget</c>.</param>
/// <param name="Arguments">Its arguments, already split, so nothing has to be re-quoted anywhere.</param>
/// <param name="Note">One sentence about what this will do to the machine — what it installs globally,
/// what it needs first. Shown beside the command, never instead of it.</param>
/// <remarks>
/// <para>A plan rather than a method that installs, and that is the whole point: this writes outside
/// every directory the application owns, sometimes with elevation, and differs per operating system.
/// It is shown first and run in a visible terminal tile — never silently, and never anywhere the user
/// cannot see what it did.</para>
/// <para>Split arguments rather than one command line because the two consumers want different things:
/// the terminal tile that runs it needs an argv, and the confirmation that precedes it needs something
/// to print. Deriving the printed form from the argv keeps them from disagreeing, which one string
/// parsed two ways could not.</para>
/// </remarks>
public sealed record InstallPlan(string Executable, IReadOnlyList<string> Arguments, string Note)
{
    /// <summary>Whether this has to run in a terminal tile the user can type into, rather than as a
    /// background process.</summary>
    /// <remarks>
    /// <para><b>False for an install and true for a sign-in</b>, which is the whole of the
    /// distinction. An install is a command that runs to an exit code; a sign-in only *starts* there —
    /// the CLI prints a URL, waits for a code, and the user answers it. Run in the background that is
    /// a process hung on a prompt nobody can see, and a row that goes on saying "not signed in"
    /// forever.</para>
    /// <para>Stated on the plan rather than inferred from <see cref="Arguments"/> being empty, which is
    /// what the two sign-in plans happen to look like today: that is a coincidence of how they are
    /// built, and reading it as the rule would put the next single-token install command — a plan whose
    /// executable needs no arguments — into a tile nobody asked for, or worse, the reverse.</para>
    /// </remarks>
    public bool NeedsATerminal { get; init; }

    /// <summary>The command as the user will see it before agreeing to it.</summary>
    /// <remarks>Naive quoting — a space means quotes — because that is what makes it readable, and it
    /// is never what runs: <c>InstallCommand.For</c> composes that, from <see cref="Arguments"/> and
    /// the shell's own quoting. It <em>was</em> what ran, which is how a plan whose executable is an
    /// already-composed shell line reached a tile wrapped in quotes and was printed instead of
    /// executed.</remarks>
    public string CommandLine =>
        string.Join(' ', new[] { Executable }.Concat(Arguments)
            .Select(part => part.Contains(' ') ? $"\"{part}\"" : part));
}
