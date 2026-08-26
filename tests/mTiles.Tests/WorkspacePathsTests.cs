using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Moving a workspace's state directory to the name the application uses after being renamed.
/// <para>This one is in <b>somebody else's repository</b>, which is what makes it worth pinning: the
/// content may be committed, so the difference between a move and a fresh directory is the difference
/// between a rename in their next <c>git status</c> and a set of notes nothing opens any more.</para>
/// </summary>
public class WorkspacePathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-ws-" + Guid.NewGuid().ToString("N"));

    public WorkspacePathsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a locked temp dir is not a failure */ }
        GC.SuppressFinalize(this);
    }

    private string Legacy => Path.Combine(_dir, WorkspacePaths.LegacyDirName);
    private string Current => Path.Combine(_dir, WorkspacePaths.DirName);

    [Fact]
    public void An_old_directory_is_moved_rather_than_left_behind()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "notes"));
        File.WriteAllText(Path.Combine(Legacy, "notes", "a.md"), "kept");

        var resolved = WorkspacePaths.Dir(_dir);

        Assert.Equal(Current, resolved);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(Current, "notes", "a.md")));
    }

    [Fact]
    public void A_workspace_with_both_is_left_exactly_as_it_is()
    {
        // It has been opened by both versions. Merging two sets of notes is a decision no code here can
        // make correctly, and the wrong guess overwrites work — so it makes none.
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Legacy, "old.md"), "old");
        File.WriteAllText(Path.Combine(Current, "new.md"), "new");

        WorkspacePaths.Dir(_dir);

        Assert.True(File.Exists(Path.Combine(Legacy, "old.md")));
        Assert.True(File.Exists(Path.Combine(Current, "new.md")));
    }

    [Fact]
    public void A_workspace_with_neither_gets_a_path_and_no_directory()
    {
        // Callers create it when they write. A read that made the directory would put an empty
        // `.mtiles/` into every repository the user so much as opened a tile in.
        var resolved = WorkspacePaths.Dir(_dir);

        Assert.Equal(Current, resolved);
        Assert.False(Directory.Exists(Current));
        Assert.False(Directory.Exists(Legacy));
    }

    /// <summary>
    /// Whether the old directory is still there is a question with its own answer, because
    /// <see cref="WorkspacePaths.Dir"/> is allowed to leave it.
    /// </summary>
    /// <remarks>
    /// The Git tile removes the old <c>.gitignore</c> entry on the strength of this. Asking "did we
    /// migrate" instead — or asking nothing at all — un-ignores a directory that is still on disk in
    /// the two cases the migration deliberately declines: a workspace opened by both versions, and a
    /// move that failed. The user's own repository then shows this application's notes and transcripts
    /// as untracked files, which is what the entry exists to prevent.
    /// </remarks>
    [Fact]
    public void The_old_directory_is_reported_as_present_whenever_it_is()
    {
        Assert.False(WorkspacePaths.LegacyDirExists(_dir));

        Directory.CreateDirectory(Legacy);
        Assert.True(WorkspacePaths.LegacyDirExists(_dir));

        // Both present: Dir declines to merge them, so it is still there afterwards and still says so.
        Directory.CreateDirectory(Current);
        WorkspacePaths.Dir(_dir);
        Assert.True(WorkspacePaths.LegacyDirExists(_dir));
    }

    [Fact]
    public void The_parts_of_a_path_are_joined_under_the_same_directory()
    {
        Assert.Equal(Path.Combine(Current, "goals", "x.json"),
            WorkspacePaths.Combine(_dir, "goals", "x.json"));
    }
}
