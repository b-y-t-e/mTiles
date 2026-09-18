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
    /// <remarks>
    /// <para><b>The file, not the name.</b> Measured on Windows PowerShell 5.1: <c>Get-Command npm
    /// -All</c> answers <c>ExternalScript npm.ps1</c> before <c>Application npm.cmd</c>, and a bare
    /// <c>npm</c> therefore runs the script — which the default <c>Restricted</c> execution policy
    /// refuses to load. Handed the path this machine actually found (<c>.exe</c> first, then the
    /// <c>.cmd</c> shim — <c>ExecutableFinder.Anywhere</c> never answers with a <c>.ps1</c> at all),
    /// the question of which file to run does not arise, and no policy applies to it.</para>
    /// <para>A path we do not have falls back to the name, which is what this always did. That is the
    /// case where the binary is not on our <c>PATH</c> either, so the tile is about to fail anyway —
    /// and failing with the shell's own "not recognized" reads better than failing on a path that was
    /// invented here.</para>
    /// <para><b>The call operator, because a quoted first token is a string here.</b> Measured:
    /// <c>'npm' 'install'</c> answers <c>Unexpected token ''install'' in expression or statement</c> —
    /// the parser, before anything is launched. <c>&amp;</c> is what says "this string names a
    /// program", and it is also the only form that survives a path with a space in it.</para>
    /// <para><b>A batch file is taken only for arguments <c>cmd.exe</c> cannot misread.</b> A
    /// <c>.cmd</c> shim passes its arguments through <c>cmd.exe</c>, which reads <c>&amp;</c>,
    /// <c>|</c>, <c>^</c> and <c>%VAR%</c> in them after PowerShell has taken its own quotes off — so
    /// a session id such as <c>x&amp;calc</c>, quoted correctly for this shell, would still run
    /// <c>calc</c>. No escaping survives both parsers reliably, so an argument outside
    /// <see cref="ShellArgument.IsBatchSafe"/> falls back to the name: the <c>.ps1</c> shim, which hands
    /// the arguments to node without a second parser — and which a <c>Restricted</c> machine refuses,
    /// failing closed rather than running a second command.</para>
    /// </remarks>
    public override string Program(string name, string? path, IReadOnlyList<string> arguments) =>
        path is { Length: > 0 } && (!IsBatchFile(path) || arguments.All(ShellArgument.IsBatchSafe))
            ? "& " + Quote(path)
            : name;

    private static bool IsBatchFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat";

    protected override string Assign(string name, string value) => $"$env:{name} = {Quote(value)}";

    /// <summary>
    /// <c>Remove-Item</c>, quietly: removing a variable that is not there is an error in PowerShell,
    /// and unsetting something the parent happens not to have is the ordinary case rather than a fault.
    /// </summary>
    protected override string Remove(string name) =>
        $"Remove-Item -LiteralPath Env:{name} -ErrorAction SilentlyContinue";
}
