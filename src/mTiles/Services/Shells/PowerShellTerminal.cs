namespace mTiles.Services.Shells;

/// <summary>
/// PowerShell — <c>pwsh</c> where it is installed, and Windows' in-box <c>powershell.exe</c> otherwise.
/// </summary>
/// <remarks>One class for both, because nothing this interface answers differs between them: the flags,
/// the quoting and the <c>$env:</c> syntax are the same, and which binary was found is the
/// installation's business rather than the shell's.</remarks>
public sealed class PowerShellTerminal : ShellTerminal
{
    public override string Id => "powershell";
    public override string DisplayName => "PowerShell";
    public override string IconId => "powershell";

    /// <summary>None. PowerShell starts interactive when it is given no command, and every flag that
    /// would say so explicitly differs between <c>pwsh</c> and <c>powershell.exe</c>.</summary>
    public override IReadOnlyList<string> InteractiveArgs => [];

    public override IReadOnlyList<string> CommandArgs => ["-Command"];

    public override IReadOnlyList<string> NoProfileArgs => ["-NoProfile"];

    /// <summary><c>pwsh</c> first: a machine with both has chosen to install the newer one.</summary>
    public override IReadOnlyList<string> DetectPaths() =>
        OperatingSystem.IsWindows()
            ? ["pwsh.exe", "powershell.exe"]
            : ["pwsh"];

    /// <summary>
    /// Single quotes, in which PowerShell expands nothing and escapes nothing — the only character
    /// needing attention is the quote itself, which is written twice.
    /// </summary>
    /// <remarks>Not double quotes, and that is the point: inside those, <c>$</c> interpolates and a
    /// backtick escapes, so a value carrying either would be read as script rather than as text.</remarks>
    public override string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <inheritdoc />
    /// <remarks><b>The call operator, because a quoted first token is a string here.</b> Measured:
    /// <c>'npm' 'install' '-g' '@anthropic-ai/claude-code'</c> answers
    /// <c>Unexpected token ''install'' in expression or statement</c> — the parser, before anything is
    /// launched. <c>&amp;</c> is what says "this string names a program", and it is also the only form
    /// that survives an executable path with a space in it.</remarks>
    public override string Invoke(string executable, IReadOnlyList<string> arguments) =>
        "& " + base.Invoke(executable, arguments);

    protected override string Assign(string name, string value) => $"$env:{name} = {Quote(value)}";

    /// <summary>
    /// <c>Remove-Item</c>, quietly: removing a variable that is not there is an error in PowerShell,
    /// and unsetting something the parent happens not to have is the ordinary case rather than a fault.
    /// </summary>
    protected override string Remove(string name) =>
        $"Remove-Item -LiteralPath Env:{name} -ErrorAction SilentlyContinue";
}
