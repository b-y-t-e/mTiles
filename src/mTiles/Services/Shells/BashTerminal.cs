namespace mTiles.Services.Shells;

/// <summary>The system bash, on the platforms that have one.</summary>
public sealed class BashTerminal : PosixShellTerminal
{
    public override string Id => "bash";
    public override string DisplayName => "bash";

    /// <summary>Both, because they cover different halves of the trap: <c>--noprofile</c> is the login
    /// shell's files and <c>--norc</c> the interactive shell's.</summary>
    public override IReadOnlyList<string> NoProfileArgs => ["--noprofile", "--norc"];

    /// <summary>Windows is Git Bash's — see <see cref="GitBashTerminal"/>. A bare <c>bash.exe</c> on
    /// <c>PATH</c> there is usually Git's own or WSL's shim, and offering it twice under two names
    /// helps nobody.</summary>
    public override IReadOnlyList<string> DetectPaths() =>
        OperatingSystem.IsWindows() ? [] : ["/bin/bash", "/usr/bin/bash", "bash"];
}
