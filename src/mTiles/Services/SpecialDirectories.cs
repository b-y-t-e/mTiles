namespace mTiles.Services;

/// <summary>
/// The directories a workspace can sit in that are not ordinary project folders — the user's home,
/// the root of a drive, and the places the operating system owns.
/// </summary>
/// <remarks>
/// <para>Pure and separate from the panel that asks, because what it answers is a fact about a path
/// rather than about a row: the same rule decides what the default workspace is called and where the
/// offer to run <c>git init</c> is withheld, and two spellings of "is this the home directory" is two
/// chances for the name and the offer to disagree.</para>
/// <para>Comparison is by <see cref="Path.GetFullPath(string)"/> with the trailing separator taken off,
/// so <c>C:\Users\me</c>, <c>C:\Users\me\</c> and <c>C:\Users\me\.\</c> are one directory. Case is
/// ignored on Windows only — two paths differing in case are two directories on Linux.</para>
/// </remarks>
public static class SpecialDirectories
{
    /// <summary>The user's own directory, or empty when the platform will not say.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Whether a path is the user's home directory itself.</summary>
    public static bool IsHome(string path) => IsSameDirectory(path, Home);

    /// <summary>
    /// Whether creating a git repository in this directory is a reasonable thing to offer.
    /// </summary>
    /// <remarks>
    /// A repository at the home directory tracks everything the user owns — every download, every
    /// application's configuration — and the first <c>git status</c> in it takes minutes. The root of a
    /// drive and the system directories are the same mistake, one step larger. The offer is withheld
    /// rather than the action refused: nothing stops a user typing <c>git init</c> there themselves,
    /// this only stops the panel suggesting it.
    /// </remarks>
    public static bool AllowsRepository(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0) return false;
        if (IsHome(normalized) || IsRoot(normalized)) return false;
        return !SystemDirectories().Any(system => IsInside(normalized, system));
    }

    /// <summary>Whether two paths name the same directory.</summary>
    private static bool IsSameDirectory(string path, string other)
    {
        var left = Normalize(path);
        var right = Normalize(other);
        return left.Length > 0 && right.Length > 0 && left.Equals(right, PathComparison);
    }

    /// <summary>Whether a normalized path is a directory itself, or anything under it.</summary>
    private static bool IsInside(string normalizedPath, string ancestor)
    {
        var root = Normalize(ancestor);
        if (root.Length == 0) return false;
        if (normalizedPath.Equals(root, PathComparison)) return true;
        return normalizedPath.StartsWith(root, PathComparison)
               && normalizedPath[root.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>Whether a normalized path is a filesystem root itself.</summary>
    /// <remarks>
    /// The root is compared after trimming its separators and <em>not</em> after
    /// <see cref="Normalize"/>: <c>Path.GetFullPath("C:")</c> is the current directory <em>on drive
    /// C:</em>, not the drive's root, so normalizing it again made the answer depend on where the
    /// process happened to be started — <c>C:\</c> counted as a root only while the process ran
    /// on another drive, and an installed copy (whose working directory is on the system drive) offered
    /// to run <c>git init</c> in <c>C:\</c>, which is the one case the rule exists to refuse.
    /// </remarks>
    private static bool IsRoot(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath);
        return root != null && TrimSeparators(root).Equals(normalizedPath, PathComparison);
    }

    /// <summary>The places the operating system owns, including everything under them.</summary>
    /// <remarks>
    /// Read from the platform on Windows rather than spelled out, because the Windows directory is not
    /// always <c>C:\Windows</c> and a localized <c>Program Files</c> is a real installation. The Linux
    /// list is the Filesystem Hierarchy Standard's, which is spelled out because it is fixed by that
    /// standard and the platform offers nothing to ask.
    /// </remarks>
    private static IEnumerable<string> SystemDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield break;
        }

        foreach (var directory in LinuxSystemDirectories)
            yield return directory;
    }

    private static readonly string[] LinuxSystemDirectories =
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/proc", "/sbin", "/sys", "/usr", "/var"];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// A path in the one form everything here compares: absolute, with no trailing separator.
    /// </summary>
    /// <remarks>
    /// An unusable path — empty, or one <see cref="Path.GetFullPath(string)"/> refuses — comes back
    /// empty rather than throwing, because every caller is answering a question about a row on screen
    /// and "we cannot tell" has to be one of the answers.
    /// </remarks>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }

        return TrimSeparators(full);
    }

    /// <summary>A path without its trailing separators.</summary>
    /// <remarks>
    /// A filesystem root trims away to nothing on Linux (<c>/</c>) and to a bare drive on Windows
    /// (<c>C:</c>). Keeping the untrimmed form for the first is what stops <c>/</c> and <c>""</c> being
    /// the same answer.
    /// </remarks>
    private static string TrimSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
