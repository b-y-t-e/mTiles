namespace mTiles.Services.Shells;

/// <summary>
/// One shell, as behaviour rather than as a row of user-editable strings.
/// </summary>
/// <remarks>
/// <para>Keyed by a <b>string id</b> the way <c>TileKindIds</c> is, not by an enum: adding a shell must
/// be one new class and one line in <see cref="ShellTerminalCatalog"/>, not an enum member plus every
/// switch that reads it. That is what the <c>ShellType</c> enum cost, and it is why <c>cmd</c> could
/// only be removed by deleting the enum with it.</para>
/// <para><b>Why the environment members are here and not only in <c>PtyOptions.Environment</c>.</b>
/// Anything secret belongs in the process environment — a startup script is <em>typed into a live
/// PTY</em>, so it lands in the scrollback and in the shell's history file. But the environment block
/// is built by merging over the parent's, so it can only ever <em>add</em>: a machine carrying a global
/// <c>ANTHROPIC_API_KEY</c> cannot be given a child that authenticates some other way. A shell can
/// (<c>Remove-Item Env:X</c>, <c>unset X</c>), and <see cref="NoProfileArgs"/> covers the other half of
/// the same trap — the user's own profile overwriting what we set.</para>
/// <para>Nothing here touches the filesystem except <see cref="DetectPaths"/>, so quoting and the
/// environment syntax are readable in a table test on any machine.</para>
/// </remarks>
public interface IShellTerminal
{
    /// <summary>Stable, lowercase, and what settings and layouts store — <c>"powershell"</c>,
    /// <c>"gitbash"</c>. Never shown to the user.</summary>
    string Id { get; }

    /// <summary>What the user is shown — <c>"PowerShell"</c>, <c>"Git Bash"</c>.</summary>
    string DisplayName { get; }

    /// <summary>The icon name, resolved to a glyph by <c>TileIcons</c>.</summary>
    string IconId { get; }

    /// <summary>
    /// Where this shell might be, best first.
    /// </summary>
    /// <returns>
    /// A mix of two things, told apart by whether the entry contains a directory separator: an absolute
    /// path is checked for existence, a bare binary name is looked up on <c>PATH</c>. Empty when this
    /// shell cannot be on this platform at all, which is how one catalog serves both.
    /// </returns>
    IReadOnlyList<string> DetectPaths();

    /// <summary>The flags that start this shell as a login/interactive session — the tile's own shell.</summary>
    IReadOnlyList<string> InteractiveArgs { get; }

    /// <summary>The flags that make it run one command and exit — <c>-c</c>, <c>-Command</c>.
    /// Deliberately without <see cref="InteractiveArgs"/>: <c>-i</c> together with <c>-c</c> asks for a
    /// shell that is both at once.</summary>
    IReadOnlyList<string> CommandArgs { get; }

    /// <summary>The flags that stop it reading the user's own startup files, so that what we set in the
    /// environment is not overwritten by a profile we cannot see.</summary>
    IReadOnlyList<string> NoProfileArgs { get; }

    /// <summary>One value, quoted so this shell reads it back literally — quotes, <c>$</c>, <c>%</c>,
    /// spaces and newlines included.</summary>
    string Quote(string value);

    /// <summary>
    /// How this shell is told to run the program called <paramref name="name"/>, as the head of a
    /// command line the rest of the arguments follow.
    /// </summary>
    /// <param name="name">The binary's own name — <c>claude</c>, <c>opencode</c>. Never a path.</param>
    /// <param name="path">Where this machine found that binary (<c>ExecutableFinder</c>,
    /// <c>AiAgentCatalog.Locate</c>), or null where it was not found at all.</param>
    /// <param name="arguments">What will follow on the command line, unquoted. Part of the question
    /// because the file decides who parses them: a <c>.cmd</c> shim hands them to <c>cmd.exe</c>, which
    /// reads <c>&amp;</c>, <c>|</c> and <c>%VAR%</c> in them whatever this shell's quoting said.</param>
    /// <remarks><b>Because a name does not name a file, and on PowerShell it names the wrong one.</b>
    /// npm installs three shims per tool on Windows — <c>claude</c>, <c>claude.cmd</c> and
    /// <c>claude.ps1</c> — and PowerShell prefers the <c>.ps1</c>. On a machine whose execution policy
    /// is <c>Restricted</c>, which is Windows' own default, it then refuses to load it: <c>claude.ps1
    /// cannot be loaded because running scripts is disabled on this system</c>, on a machine where the
    /// CLI is installed and this application has just said so. Every agent tile and the Sign in button
    /// failed that way on a fresh Windows.
    /// <para>Which file a name runs is this interface's question for the same reason <see cref="Quote"/>
    /// is: the answer differs per shell and nothing above it should have to
    /// know that PowerShell is the one that needs telling. <em>Where</em> the binary is stays this
    /// application's answer — a path is a fact about the machine, not about the shell — which is why it
    /// is a parameter rather than something a shell goes and looks up.</para>
    /// <para>Deliberately not "loosen the execution policy for the process we start". That would work,
    /// and it would also let every other <c>.ps1</c> in that tile run on a machine where an
    /// administrator switched scripts off on purpose — while still failing wherever the policy comes
    /// from group policy, which is exactly the managed machine it would be weakening.</para></remarks>
    string Program(string name, string? path, IReadOnlyList<string> arguments);

    /// <summary>The statement that gives an environment variable a value in this shell.</summary>
    string SetEnv(string name, string value);

    /// <summary>The statement that removes one, <em>including one inherited from the parent</em> — the
    /// half of the job the environment block cannot do.</summary>
    string UnsetEnv(string name);

    /// <summary>
    /// <paramref name="command"/> preceded by the statements that put <paramref name="variables"/> in
    /// place, as one command line for <see cref="CommandArgs"/>.
    /// </summary>
    /// <param name="variables">A null value means <em>unset</em>, matching the dictionary an agent
    /// hands out. Anything else is set to that value.</param>
    string WithEnv(IReadOnlyDictionary<string, string?> variables, string command);
}
