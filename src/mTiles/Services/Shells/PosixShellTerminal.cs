namespace mTiles.Services.Shells;

/// <summary>
/// What bash, zsh and Git Bash agree on: <c>-c</c>, single-quoted values, <c>export</c> and
/// <c>unset</c>. They differ only in where they live and which flag turns their startup files off.
/// </summary>
public abstract class PosixShellTerminal : ShellTerminal
{
    public override string IconId => "bash";

    public override IReadOnlyList<string> InteractiveArgs => ["-l"];

    public override IReadOnlyList<string> CommandArgs => ["-c"];

    /// <summary>
    /// Single quotes, with the one character they cannot contain spliced in from outside them.
    /// </summary>
    /// <remarks>Inside single quotes a POSIX shell expands nothing at all — <c>$</c>, backticks,
    /// <c>%</c>, spaces and newlines are literal — so the only case to handle is the quote itself:
    /// close, escape it, reopen (<c>'\''</c>).</remarks>
    public override string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    protected override string Assign(string name, string value) => $"export {name}={Quote(value)}";

    protected override string Remove(string name) => $"unset {name}";
}
