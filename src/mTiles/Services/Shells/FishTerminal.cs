namespace mTiles.Services.Shells;

/// <summary>
/// fish — a POSIX-<em>style</em> shell that is not a POSIX shell, which is exactly why it is not a
/// <see cref="PosixShellTerminal"/>: it escapes inside single quotes, and it has neither
/// <c>export</c> nor <c>unset</c>.
/// </summary>
public sealed class FishTerminal : ShellTerminal
{
    public override string Id => "fish";
    public override string DisplayName => "fish";
    public override string IconId => "fish";

    public override IReadOnlyList<string> InteractiveArgs => ["-l"];
    public override IReadOnlyList<string> CommandArgs => ["-c"];
    public override IReadOnlyList<string> NoProfileArgs => ["--no-config"];

    public override IReadOnlyList<string> DetectPaths() =>
        OperatingSystem.IsWindows() ? [] : ["/usr/bin/fish", "/usr/local/bin/fish", "/bin/fish", "fish"];

    /// <summary>
    /// Single quotes, in which fish still recognises two escapes — <c>\'</c> and <c>\\</c> — so the
    /// backslash has to be doubled before the quote is escaped with one.
    /// </summary>
    /// <remarks>The order matters and is the whole of the difference from
    /// <see cref="PosixShellTerminal.Quote"/>: escaping the quote first would then have its own
    /// backslash doubled, and the value would come back with a stray one in it.</remarks>
    public override string Quote(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary><c>-g</c> so it outlives the block and <c>-x</c> so a child process inherits it —
    /// which is the whole reason for setting it.</summary>
    protected override string Assign(string name, string value) => $"set -gx {name} {Quote(value)}";

    protected override string Remove(string name) => $"set -e {name}";
}
