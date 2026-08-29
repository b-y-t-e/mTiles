namespace mTiles.Services;

/// <summary>
/// What kind of place a workspace's directory is.
/// </summary>
/// <remarks>
/// A kind rather than a <c>bool</c>, because the panel asks two different questions of the same path:
/// whether to offer a repository, which every kind but <see cref="Ordinary"/> refuses, and which glyph
/// the row wears, where each refusal is a different picture. A single flag answered the first and left
/// the second to a second reading of the path.
/// </remarks>
public enum SpecialDirectoryKind
{
    /// <summary>A project folder — anywhere this application has no opinion about.</summary>
    Ordinary,

    /// <summary>The user's own directory itself, not something under it.</summary>
    Home,

    /// <summary>The desktop.</summary>
    Desktop,

    /// <summary>The user's documents folder.</summary>
    Documents,

    /// <summary>The user's downloads folder.</summary>
    Downloads,

    /// <summary>The user's pictures folder.</summary>
    Pictures,

    /// <summary>The user's music folder.</summary>
    Music,

    /// <summary>The user's videos folder.</summary>
    Videos,

    /// <summary>The root of a drive, or <c>/</c>.</summary>
    DriveRoot,

    /// <summary>A directory the operating system owns, or anything under one.</summary>
    System,

    /// <summary>A path nothing can make sense of. Not a project folder, and not one of the named
    /// places either.</summary>
    Unknown,
}

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
    public static bool IsHome(string path) => Kind(path) == SpecialDirectoryKind.Home;

    /// <summary>
    /// Which kind of place this path is.
    /// </summary>
    /// <remarks>
    /// <para>The one answer everything else here is derived from, and it exists because the panel
    /// started needing to say <em>which</em> kind rather than only whether it was one: the row draws a
    /// glyph from it and the offer to run <c>git init</c> is withheld for every kind but
    /// <see cref="SpecialDirectoryKind.Ordinary"/>, and two spellings of "is this the home directory"
    /// is two chances for the glyph and the offer to disagree about the same row.</para>
    /// <para><b>Order matters, and the home directory is asked first.</b> On Unix it is also the
    /// platform's answer for Documents — see <see cref="UserFolders"/> — and Home is the truer of the
    /// two: what the row is sitting in is the user's own directory, whatever else the platform also
    /// calls it.</para>
    /// <para><see cref="SpecialDirectoryKind.Unknown"/> is an answer of its own rather than a fall to
    /// <see cref="SpecialDirectoryKind.Ordinary"/>: every caller is deciding something about a row on
    /// screen, and a path <see cref="Normalize"/> cannot make sense of must not be told it is an
    /// ordinary project folder — that is the reading that puts an offer to write to somewhere unknown
    /// on a row.</para>
    /// </remarks>
    public static SpecialDirectoryKind Kind(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0) return SpecialDirectoryKind.Unknown;
        if (IsSameDirectory(normalized, Home)) return SpecialDirectoryKind.Home;

        foreach (var (folder, kind) in UserFolders())
            if (IsSameDirectory(normalized, folder)) return kind;

        if (IsRoot(normalized)) return SpecialDirectoryKind.DriveRoot;
        return SystemDirectories().Any(system => IsInside(normalized, system))
            ? SpecialDirectoryKind.System
            : SpecialDirectoryKind.Ordinary;
    }

    /// <summary>
    /// The folders the operating system made for the user's own files.
    /// </summary>
    /// <remarks>
    /// <para>Matched as the directory <em>itself</em> and never as an ancestor, which is the whole
    /// difference between these and <see cref="SystemDirectories"/>: a project under
    /// <c>~/Documents</c> is an ordinary project and gets everything an ordinary project gets, while
    /// <c>~/Documents</c> itself is a place to keep files rather than a thing to version. A repository
    /// at its root tracks every file the user has ever put there, which is the home directory's problem
    /// one step smaller.</para>
    /// <para>Read from the platform rather than spelled out, because these are localized and
    /// relocatable — a Windows user can move Downloads to another drive, and on Linux they come from
    /// XDG. <b>Two are guessed by name under the home directory</b>, and both because the platform
    /// will not say. Downloads has no <see cref="Environment.SpecialFolder"/> at all. Documents has
    /// one, and <b>on Unix .NET answers it with the home directory itself</b> (<c>MyDocuments</c> is
    /// <c>Personal</c>, which is <c>$HOME</c> there) — so on Linux the folder people actually keep
    /// documents in was matched by nothing, and <c>~/Documents</c> was the one of these six offered a
    /// repository. That mapping is also why <see cref="Kind"/> answers
    /// <see cref="SpecialDirectoryKind.Home"/> rather than <see cref="SpecialDirectoryKind.Documents"/>
    /// for <c>MyDocuments</c> on Linux, which is correct: the path <em>is</em> the home directory.</para>
    /// <para>A guess that misses — a localized <c>Dokumenty</c>, a relocated Downloads — is simply not
    /// found, and a folder that is not found is an ordinary one. That is the safe way round: the only
    /// thing riding on it is whether the panel offers to run <c>git init</c>.</para>
    /// <para>A folder the platform answers with the home directory is skipped rather than yielded.
    /// Nothing depends on that today — <see cref="Kind"/> asks about Home first — but an entry
    /// claiming the home directory is Documents is a wrong answer waiting for the order of two lines
    /// to change.</para>
    /// <para>An empty answer matches nothing: <see cref="IsSameDirectory"/> refuses a blank on either
    /// side, so a platform that will not say does not quietly make every row a special folder.</para>
    /// </remarks>
    private static IEnumerable<(string Path, SpecialDirectoryKind Kind)> UserFolders()
    {
        foreach (var (folder, kind) in PlatformFolders())
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && !IsSameDirectory(path, Home))
                yield return (path, kind);
        }

        var home = Home;
        if (home.Length == 0) yield break;

        yield return (Path.Combine(home, "Downloads"), SpecialDirectoryKind.Downloads);

        // Only where the platform's own answer was the home directory, which is Unix. On Windows
        // MyDocuments is the folder itself — and a user who has moved it is followed by the platform
        // and would not be by a name.
        if (IsSameDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), home))
            yield return (Path.Combine(home, "Documents"), SpecialDirectoryKind.Documents);
    }

    /// <summary>The folders the platform will name, in the order they are asked about.</summary>
    private static IEnumerable<(Environment.SpecialFolder Folder, SpecialDirectoryKind Kind)>
        PlatformFolders()
    {
        yield return (Environment.SpecialFolder.DesktopDirectory, SpecialDirectoryKind.Desktop);
        yield return (Environment.SpecialFolder.MyDocuments, SpecialDirectoryKind.Documents);
        yield return (Environment.SpecialFolder.MyPictures, SpecialDirectoryKind.Pictures);
        yield return (Environment.SpecialFolder.MyMusic, SpecialDirectoryKind.Music);
        yield return (Environment.SpecialFolder.MyVideos, SpecialDirectoryKind.Videos);
    }

    /// <summary>
    /// Whether creating a git repository in this directory is a reasonable thing to offer.
    /// </summary>
    /// <remarks>
    /// <para>A repository at the home directory tracks everything the user owns — every download, every
    /// application's configuration — and the first <c>git status</c> in it takes minutes. The root of a
    /// drive and the system directories are the same mistake, one step larger; Desktop, Documents,
    /// Downloads, Pictures, Music and Videos are the same mistake one step smaller, and they are the
    /// folders somebody browsing for a workspace is most likely to land in by accident.</para>
    /// <para>The offer is withheld rather than the action refused: nothing stops a user typing
    /// <c>git init</c> there themselves, this only stops the panel suggesting it. And only the folder
    /// itself — a project under <c>~/Documents</c> is an ordinary project.</para>
    /// </remarks>
    public static bool AllowsRepository(string path) => Kind(path) == SpecialDirectoryKind.Ordinary;

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
