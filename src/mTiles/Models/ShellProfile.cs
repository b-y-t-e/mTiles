namespace mTiles.Models;

public sealed class ShellProfile
{
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
    public ShellType Type { get; init; } = ShellType.Other;

    /// <summary>
    /// How to run a single command in this shell and have it exit afterwards, rather than starting it
    /// interactively — <c>pwsh -Command "…"</c>, <c>cmd /c "…"</c>, <c>bash -c "…"</c>.
    /// <para>Here because it is a property of the shell, and the shell is what this type is. A new shell
    /// is then one entry in <see cref="ShellType"/> plus one line here, instead of a switch that every
    /// caller wanting to run a command has to grow its own copy of.</para>
    /// <para><see cref="Args"/> is deliberately not included: those are the interactive-startup flags
    /// (<c>--login -i</c>, <c>-l</c>) and this is the non-interactive form — <c>-i</c> together with
    /// <c>-c</c> in particular asks for a shell that is both at once. The trade-off is that a login
    /// profile's PATH is not applied to the command; that is why the fallback chain ends by starting
    /// the shell interactively, with <see cref="Args"/>.</para>
    /// </summary>
    public (string Executable, string[] Args) CommandLine(string command) =>
        (ExecutablePath, [CommandFlag, command]);

    private string CommandFlag => Type switch
    {
        ShellType.Cmd => "/c",
        ShellType.PowerShell => "-Command",
        _ => "-c",   // bash, zsh, fish and every other POSIX-style shell
    };
}
