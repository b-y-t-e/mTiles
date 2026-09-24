namespace mTiles.Services;

/// <summary>
/// Finding a program on <c>PATH</c>, for the callers that cannot rely on the process' own resolution.
/// </summary>
/// <remarks>
/// A GUI application does not inherit the <c>PATH</c> a login shell would have assembled, so "just run
/// it and see" answers "not installed" for tools that are. This is the scan that used to live on
/// <c>ShellDetector</c> and was called by things that are not shells at all — git, the AI tools — which
/// is why it is here under its own name rather than following the shells into
/// <c>Services/Shells/</c>.
/// </remarks>
internal static class ExecutableFinder
{
    /// <summary>The full path of <paramref name="name"/> on <c>PATH</c>, or null.</summary>
    public static string? OnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;

            // A PATH entry can be anything the user has ever typed, including characters that are not
            // legal in a path at all. Combine throws on those, and one bad entry must not stop the scan
            // at the directory the tool is actually in.
            string full;
            try { full = Path.Combine(dir, name); }
            catch (ArgumentException) { continue; }

            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// The full path of a program named without an extension, looked for everywhere this application
    /// knows to look — or null.
    /// </summary>
    /// <remarks>
    /// <para><c>PATH</c> first, with the extensions Windows needs (<c>.exe</c>, and the <c>.cmd</c>
    /// shim npm installs, which is what most of these are), then the handful of directories a global
    /// npm, go, cargo or per-tool install writes to. The second half is not belt and braces: a windowed
    /// process does not inherit the <c>PATH</c> a login shell assembles, so a tool the user installed
    /// this morning is genuinely absent from ours.</para>
    /// <para>The technique salvaged from the AI tools table, which is all that was worth keeping of it:
    /// what it scanned <em>for</em> was a row holding a binary name, and what asks now is a class with
    /// behaviour.</para>
    /// </remarks>
    public static string? Anywhere(string name)
    {
        if (!OperatingSystem.IsWindows())
            return OnPath(name) ?? InHomeDirectories(name, "");

        return OnPathRunnable(name) ?? InHomeDirectories(name, ".exe", ".cmd");
    }

    /// <summary>What a shell would run for <paramref name="name"/> typed bare: <c>PATH</c> alone, with
    /// the extensions Windows resolves a bare name through (<c>.exe</c>, <c>.cmd</c>, <c>.bat</c>).</summary>
    /// <remarks><b>Directory by directory, then extension by extension</b> — the order a shell searches
    /// in. It used to be the other way round, one pass over the whole of <c>PATH</c> per extension, so a
    /// <c>claude.exe</c> in a later directory beat the <c>claude.cmd</c> npm put in an earlier one: the
    /// tile ran a different, older CLI than the user's own terminal did, and nothing on screen said
    /// so. A bare name is still the last resort and only after every directory has been asked, because
    /// Windows cannot launch an extensionless file on its own — npm puts one beside every shim.</remarks>
    public static string? OnPathRunnable(string name) =>
        OnPathRunnable(name, Environment.GetEnvironmentVariable("PATH") ?? "", OperatingSystem.IsWindows(),
            File.Exists);

    /// <summary><see cref="OnPathRunnable(string)"/> over a given <c>PATH</c>, for a test.</summary>
    internal static string? OnPathRunnable(string name, string path, bool windows, Func<string, bool> exists) =>
        RunnableOnPath(name, path, windows, exists).FirstOrDefault()
        ?? (windows ? Candidates(name, path, [""]).FirstOrDefault(exists) : null);

    /// <summary>
    /// Every copy of <paramref name="name"/> this machine has, in the order they would be found — the
    /// first is the one that runs.
    /// </summary>
    /// <remarks>
    /// <para>For a warning and nothing else. Two installations of one CLI — an npm install and a
    /// forgotten winget one, say — mean whichever comes first on <c>PATH</c> runs everywhere, here and
    /// in the user's own shell alike, and that is kept on purpose: running the newest instead would make
    /// a tile and a terminal disagree about which CLI <c>claude</c> is. What is missing is somebody
    /// being told there is a second one.</para>
    /// <para>One entry per directory, so npm's <c>claude</c>, <c>claude.cmd</c> and <c>claude.ps1</c>
    /// are one installation and not three.</para>
    /// </remarks>
    public static IReadOnlyList<string> Installations(string name)
    {
        var windows = OperatingSystem.IsWindows();
        var found = RunnableOnPath(name, Environment.GetEnvironmentVariable("PATH") ?? "", windows, File.Exists)
            .Concat(InHomeDirectoriesAll(name, windows ? [".exe", ".cmd"] : []));

        var seen = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var candidate in found)
        {
            string directory;
            try { directory = RealDirectory(Path.GetDirectoryName(candidate) ?? candidate); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException)
            { continue; }

            if (seen.Add(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>The directory with every link resolved, so <c>/bin</c> and <c>/usr/bin</c> on a merged-usr
    /// system are one place and not two installations.</summary>
    private static string RealDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
    }

    /// <summary>Every runnable match on <paramref name="path"/>, directory by directory.</summary>
    private static IEnumerable<string> RunnableOnPath(string name, string path, bool windows,
        Func<string, bool> exists) =>
        Candidates(name, path, windows ? [".exe", ".cmd", ".bat"] : [""]).Where(exists);

    /// <summary>The file names to try, outer loop the directories and inner loop the extensions.</summary>
    private static IEnumerable<string> Candidates(string name, string path, string[] extensions)
    {
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;

            foreach (var extension in extensions)
            {
                // A PATH entry can be anything the user has ever typed, including characters that are
                // not legal in a path at all; one bad entry must not stop the scan.
                string full;
                try { full = Path.Combine(dir, name + extension); }
                catch (ArgumentException) { continue; }

                yield return full;
            }
        }
    }

    /// <summary>The places an install puts a binary without asking <c>PATH</c> about it.</summary>
    /// <param name="extensions">Tried in turn before the bare name, so a <c>.cmd</c> shim is preferred
    /// to an extensionless script Windows cannot launch on its own.</param>
    private static string? InHomeDirectories(string name, params string[] extensions) =>
        InHomeDirectoriesAll(name, extensions).FirstOrDefault();

    /// <summary>Every match in <see cref="InHomeDirectories"/>' directories, in its order.</summary>
    private static IEnumerable<string> InHomeDirectoriesAll(string name, string[] extensions)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        string[] directories =
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "go", "bin"),
            Path.Combine(home, $".{name}", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(home, ".cargo", "bin"),
            // Windows' app-execution aliases. Not a developer tool directory like the five above, and
            // it is here for the same reason they are: measured 2026-09-23 on a Windows 11 machine
            // where winget is installed and this directory is in *no* process' PATH — not the GUI's,
            // not PowerShell's, not Git Bash's — so `winget` answered "command not found" everywhere
            // while the binary sat right there. The entries are reparse points to the real package
            // under Program Files\WindowsApps, which File.Exists reports and a child process runs.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps"),
            // Where winget links a portable package's binary. It is added to the user's PATH by the
            // first portable install, which a process already running never sees — so without it a
            // tool winget has just installed would read as missing until the application restarts.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links"),
        ];

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) yield return candidate;
            }

            var bare = Path.Combine(directory, name);
            if (File.Exists(bare)) yield return bare;
        }
    }
}
