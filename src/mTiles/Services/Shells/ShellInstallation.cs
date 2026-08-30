namespace mTiles.Services.Shells;

/// <summary>
/// One shell as it exists on this machine: what it is, and where it was found.
/// </summary>
/// <param name="Shell">The behaviour — flags, quoting, environment syntax.</param>
/// <param name="ExecutablePath">The binary that was found, absolute wherever detection could make it so.</param>
/// <remarks>
/// Two types rather than one, and deliberately: <see cref="IShellTerminal"/> describes a <em>kind</em>
/// of shell and is a stateless singleton the catalog holds one of, while a path is a fact about this
/// installation. Folding the path into the interface would mean constructing a shell class per
/// detection and would put a filesystem behind every quoting test.
/// </remarks>
public sealed record ShellInstallation(IShellTerminal Shell, string ExecutablePath)
{
    /// <summary>What settings and layouts store.</summary>
    public string Id => Shell.Id;

    /// <summary>What the user is shown.</summary>
    public string DisplayName => Shell.DisplayName;

    /// <summary>The arguments that start this shell for somebody to type into.</summary>
    public IReadOnlyList<string> InteractiveArgs => Shell.InteractiveArgs;

    /// <summary>
    /// The executable and arguments that run <paramref name="command"/> non-interactively and exit.
    /// </summary>
    /// <remarks><see cref="InteractiveArgs"/> is deliberately left out: those are the startup flags
    /// (<c>--login -i</c>, <c>-l</c>), and <c>-i</c> together with <c>-c</c> asks for a shell that is
    /// both at once. The cost is that a login profile's <c>PATH</c> is not applied to the command,
    /// which is why the launch chain ends by starting the shell interactively, with those args.</remarks>
    public (string Executable, string[] Args) CommandLineFor(string command) =>
        (ExecutablePath, [.. Shell.CommandArgs, command]);
}
