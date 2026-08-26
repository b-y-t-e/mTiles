using System.Collections.Concurrent;
using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// The directory this application keeps a workspace's own state in — notes, todos, goals, the database
/// tile's configuration — inside the user's project.
/// </summary>
/// <remarks>
/// <para>One place, because there were four: the notes directory, the todos directory, the goals
/// directory and <c>databases.json</c> each spelled the name themselves, and a rename that missed one
/// would leave a tile quietly writing to a directory nothing else reads.</para>
/// <para>This is a directory <b>in somebody else's repository</b>, which is what makes the rename here
/// different in kind from the one in <see cref="AppPaths"/>. Content under the old name may be
/// committed, so moving it shows up as a rename in their next <c>git status</c> — visible and
/// reversible, which is the most this can be. It is not silent, and it is not a deletion.</para>
/// </remarks>
internal static class WorkspacePaths
{
    public const string DirName = ".mtiles";

    /// <summary>What it was called before the application was renamed. Still excluded from the Goal
    /// tile's worktree reads, because a workspace this application has never opened since the rename
    /// still has one.</summary>
    public const string LegacyDirName = ".mterminal";

    /// <summary>
    /// Workspaces already looked at in this process. The migration is one <c>Directory.Exists</c> pair
    /// and, at most once, a rename — but this is called on every note, todo, goal and database read, so
    /// without it the answer to "is there an old directory here" is a filesystem call per keystroke.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<string>> Seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The workspace's state directory, having moved the old one into place if that is what is there.
    /// The directory itself is not created — callers that write make it when they write.
    /// </summary>
    public static string Dir(string workingDirectory) =>
        // Lazy rather than TryAdd, for the reason AppPaths is: a flag set *before* the work means the
        // second caller is handed the new path while the rename is still running, and reads a directory
        // that is half there. GetOrAdd hands every caller the same Lazy and they all wait on it.
        Seen.GetOrAdd(workingDirectory,
            dir => new Lazy<string>(() =>
            {
                var current = Path.Combine(dir, DirName);
                Migrate(dir, current);
                return current;
            })).Value;

    /// <summary>
    /// Whether the directory under the old name is still on disk.
    /// </summary>
    /// <remarks>
    /// Asked before its <c>.gitignore</c> entry is removed, because <see cref="Dir"/> is allowed to
    /// leave it there: both names existing is a workspace opened by two versions and is deliberately
    /// not merged, and a move can simply fail. Removing the entry in either case un-ignores a directory
    /// that is still present, which puts this application's own notes and transcripts in front of the
    /// user as untracked files in their own repository — the exact outcome the entry exists to
    /// prevent.
    /// </remarks>
    public static bool LegacyDirExists(string workingDirectory) =>
        Directory.Exists(Path.Combine(workingDirectory, LegacyDirName));

    /// <summary>A file or subdirectory inside it.</summary>
    public static string Combine(string workingDirectory, params string[] parts) =>
        Path.Combine([Dir(workingDirectory), ..parts]);

    /// <summary>
    /// Moves the old directory to the new name, and does nothing at all if there is any doubt.
    /// </summary>
    /// <remarks>
    /// Both existing is left alone deliberately: it means this workspace has been opened by both
    /// versions, and merging two sets of notes is a decision no code here can make. A failure is
    /// swallowed to a warning for the reason <see cref="AppPaths"/> gives — except that here the cost
    /// of failing is smaller and the cost of guessing is larger, since the files are the user's own and
    /// under their version control.
    /// </remarks>
    private static void Migrate(string workingDirectory, string current)
    {
        try
        {
            var legacy = Path.Combine(workingDirectory, LegacyDirName);
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return;

            Directory.Move(legacy, current);
            Trace.TraceInformation($"Moved {legacy} to {current}.");
        }
        catch (Exception ex)
        {
            // Not rethrown, and the new path is still what is returned: a workspace whose old directory
            // could not be moved gets a fresh one beside it rather than a tile that refuses to open.
            // Nothing is deleted either way, so the notes are where they were.
            Trace.TraceWarning($"Could not move the workspace state directory in {workingDirectory}: {ex.Message}");
        }
    }
}
