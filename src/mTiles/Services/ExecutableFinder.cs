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

        return OnPath(name + ".exe")
            ?? OnPath(name + ".cmd")
            ?? OnPath(name + ".bat")
            ?? OnPath(name)
            ?? InHomeDirectories(name, ".exe", ".cmd");
    }

    /// <summary>The places an install puts a binary without asking <c>PATH</c> about it.</summary>
    /// <param name="extensions">Tried in turn before the bare name, so a <c>.cmd</c> shim is preferred
    /// to an extensionless script Windows cannot launch on its own.</param>
    private static string? InHomeDirectories(string name, params string[] extensions)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        string[] directories =
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "go", "bin"),
            Path.Combine(home, $".{name}", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(home, ".cargo", "bin"),
        ];

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) return candidate;
            }

            var bare = Path.Combine(directory, name);
            if (File.Exists(bare)) return bare;
        }

        return null;
    }
}
