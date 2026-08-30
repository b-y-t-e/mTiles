namespace mTiles.Services.Shells;

/// <summary>zsh — the default login shell on macOS and a common one on Linux.</summary>
public sealed class ZshTerminal : PosixShellTerminal
{
    public override string Id => "zsh";
    public override string DisplayName => "zsh";

    /// <summary>One flag, not bash's two: <c>--no-rcs</c> covers every one of zsh's startup files.</summary>
    public override IReadOnlyList<string> NoProfileArgs => ["--no-rcs"];

    public override IReadOnlyList<string> DetectPaths() =>
        OperatingSystem.IsWindows() ? [] : ["/bin/zsh", "/usr/bin/zsh", "zsh"];
}
