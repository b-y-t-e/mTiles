using mTiles.Models;

namespace mTiles.Services.Shells;

/// <summary>
/// The shells this application knows, and which of them this machine has.
/// </summary>
/// <remarks>
/// <para>The registry is one list — adding a shell is a class and a line here, which is the whole
/// argument for string ids over an enum.</para>
/// <para><b><c>cmd</c> is not in it, and that is a decision rather than an omission.</b> It cannot run
/// what this application asks a shell to run: it does not parse its command line by the
/// <c>CommandLineToArgvW</c> rules the PTY backend quotes with, it runs only the first line of a
/// multi-line command, and it does not treat <c>;</c> as a separator — all measured. It used to be
/// offered and then silently swapped for PowerShell behind the user's back, which meant a shell that
/// was neither what they picked nor what ran their commands.</para>
/// </remarks>
public static class ShellTerminalCatalog
{
    /// <summary>Every shell kind, in the order a chooser should offer them.</summary>
    public static IReadOnlyList<IShellTerminal> All { get; } =
    [
        new PowerShellTerminal(),
        new GitBashTerminal(),
        new BashTerminal(),
        new ZshTerminal(),
        new FishTerminal(),
    ];

    /// <summary>
    /// The shell a stored name refers to, or null.
    /// </summary>
    /// <param name="idOrDisplayName">An <see cref="IShellTerminal.Id"/>, or the display name written by
    /// a build that had no ids. Both are matched, because settings and layouts on disk predate the ids
    /// and a tile must come back running what it was running. A name nothing answers to — <c>CMD</c>,
    /// or a shell removed since — is a null here and a fall back to the default at the call site.</param>
    public static IShellTerminal? Find(string? idOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(idOrDisplayName)) return null;

        return All.FirstOrDefault(s => s.Id.Equals(idOrDisplayName, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(s => s.DisplayName.Equals(idOrDisplayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shells installed here, one entry per kind, in <see cref="All"/>'s order.
    /// </summary>
    /// <remarks>Walks every directory on <c>PATH</c> for the bare names and stats the fixed ones, so it
    /// is not free — <c>TileContext</c> holds the result over a window rather than asking per tile.</remarks>
    public static IReadOnlyList<ShellInstallation> Detect() =>
        [.. All.Select(shell => Locate(shell) is { } path ? new ShellInstallation(shell, path) : null)
               .OfType<ShellInstallation>()];

    /// <summary>The user's default shell, whatever the machine turns out to have.</summary>
    /// <remarks>Never null: a tile without a shell is a tile that shows nothing and explains nothing, so
    /// the last resort is the platform's own binary name and a hope that <c>PATH</c> resolves it — which
    /// is a launch that may fail and say so, rather than a launch that never happens.</remarks>
    public static ShellInstallation ResolveDefault(AppSettings settings) =>
        ResolveDefault(settings, Detect());

    /// <param name="detected">The shells to choose from, so the rule can be read without a filesystem
    /// behind it — and so a caller holding a detection already does not pay for another.</param>
    /// <inheritdoc cref="ResolveDefault(AppSettings)"/>
    public static ShellInstallation ResolveDefault(AppSettings settings,
        IReadOnlyList<ShellInstallation> detected)
    {
        if (Find(settings.DefaultShellName) is { } chosen
            && detected.FirstOrDefault(i => i.Shell.Id == chosen.Id) is { } installed)
            return installed;

        return detected.FirstOrDefault(i => i.Shell.Id == PreferredId)
            ?? detected.FirstOrDefault()
            ?? Fallback();
    }

    /// <summary>
    /// The shell a stored name asks for, or the default when it is not installed.
    /// </summary>
    /// <param name="idOrDisplayName">What a layout or a profile wrote down. See <see cref="Find"/>.</param>
    /// <param name="detected">The shells to pick from.</param>
    /// <param name="settings">Where the default comes from when the name finds nothing.</param>
    public static ShellInstallation Resolve(string? idOrDisplayName,
        IReadOnlyList<ShellInstallation> detected, AppSettings settings)
    {
        if (Find(idOrDisplayName) is { } shell
            && detected.FirstOrDefault(i => i.Shell.Id == shell.Id) is { } installed)
            return installed;

        return ResolveDefault(settings, detected);
    }

    /// <summary>What a machine gets when it has said nothing: the shell its users expect to land in.</summary>
    private static string PreferredId => OperatingSystem.IsWindows() ? "powershell" : "bash";

    /// <summary>Nothing was detected — not a state a machine reaches in practice, and not one to leave
    /// a tile dead in either.</summary>
    private static ShellInstallation Fallback()
    {
        var shell = All.First(s => s.Id == PreferredId);
        return new ShellInstallation(shell, shell.DetectPaths().FirstOrDefault() ?? shell.Id);
    }

    /// <summary>The first of a shell's candidate paths that is really there.</summary>
    /// <remarks>Which of the two kinds of candidate this is — a path to stat or a name to look up on
    /// <c>PATH</c> — is read off the string rather than declared, so a shell class names its locations
    /// in one list instead of two that have to be kept in step.</remarks>
    private static string? Locate(IShellTerminal shell)
    {
        foreach (var candidate in shell.DetectPaths())
        {
            var found = candidate.Contains(Path.DirectorySeparatorChar)
                        || candidate.Contains(Path.AltDirectorySeparatorChar)
                ? File.Exists(candidate) ? candidate : null
                : ExecutableFinder.OnPath(candidate);

            if (found is not null) return found;
        }
        return null;
    }
}
