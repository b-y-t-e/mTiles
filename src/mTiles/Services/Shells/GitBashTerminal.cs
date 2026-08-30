namespace mTiles.Services.Shells;

/// <summary>
/// The bash that comes with Git for Windows — the POSIX shell almost every Windows developer already
/// has, and the reason Windows needs no separate bash entry.
/// </summary>
public sealed class GitBashTerminal : PosixShellTerminal
{
    public override string Id => "gitbash";
    public override string DisplayName => "Git Bash";

    /// <summary><c>--login</c> is what sets up the MSYS environment this bash needs, and <c>-i</c> is
    /// what makes it a shell somebody can type into.</summary>
    public override IReadOnlyList<string> InteractiveArgs => ["--login", "-i"];

    public override IReadOnlyList<string> NoProfileArgs => ["--noprofile", "--norc"];

    /// <summary>Never on <c>PATH</c>, always in one of three places. Git's installer does not add
    /// <c>bin/</c> to <c>PATH</c> — it adds <c>cmd/</c>, which holds <c>git.exe</c> and no shell — so a
    /// name lookup finds nothing and these fixed paths are the whole of the detection.</summary>
    public override IReadOnlyList<string> DetectPaths()
    {
        if (!OperatingSystem.IsWindows()) return [];

        return
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Git", "bin", "bash.exe"),
        ];
    }
}
