using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Moving the application's own data directory to the name it uses after being renamed.
/// </summary>
/// <remarks>
/// The same four cases as <see cref="WorkspacePathsTests"/>, and worth stating twice because what is in
/// here is worth more: <c>settings.json</c> with the user's profiles, AI tool paths and DPAPI-encrypted
/// database passwords; the phone bridge's private key; and hundreds of megabytes of speech models. The
/// difference between a move and a fresh directory is the difference between an installation and a
/// first run — and the first run <em>saves</em>.
/// </remarks>
public class AppPathsTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "mtiles-app-" + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_parent);

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { /* a locked temp dir is not a failure */ }
        GC.SuppressFinalize(this);
    }

    private string Legacy => Path.Combine(_parent, "MTerminal");
    private string Current => Path.Combine(_parent, "mTiles");

    [Fact]
    public void An_old_installation_is_moved_rather_than_replaced_by_a_first_run()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), """{ "GitPath": "kept" }""");

        var resolved = AppPaths.Resolve(_parent);

        Assert.Equal(Current, resolved);
        Assert.False(Directory.Exists(Legacy));
        Assert.Contains("kept", File.ReadAllText(Path.Combine(Current, "settings.json")));
    }

    [Fact]
    public void A_machine_with_both_keeps_using_the_new_one_and_touches_neither()
    {
        // Both existing means this build has already been writing to the new directory. Merging two
        // installations — two settings files, two sets of models — is a decision no code here can make.
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "old");
        File.WriteAllText(Path.Combine(Current, "settings.json"), "new");

        Assert.Equal(Current, AppPaths.Resolve(_parent));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "settings.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Current, "settings.json")));
    }

    [Fact]
    public void A_fresh_machine_gets_the_new_path_and_no_directory_is_made()
    {
        // Making it here would be harmless but untrue to what this method is: an answer, not a setup.
        // The callers that write create what they need.
        Assert.Equal(Current, AppPaths.Resolve(_parent));
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void A_move_that_cannot_be_made_keeps_the_old_path_in_use()
    {
        // The whole safety property. Anything can hold a handle open — a scanner, a second instance, a
        // backup tool — and answering "then use the new empty path" turns a locked file into a lost
        // installation. Here the obstruction is a *file* where the new directory would go, which is
        // what Directory.Move refuses in a way no platform disagrees about.
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "kept");
        File.WriteAllText(Current, "not a directory");

        Assert.Equal(Legacy, AppPaths.Resolve(_parent));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(Legacy, "settings.json")));

        // And it says so somewhere a person can read it. The note is held rather than traced because at
        // the moment of the move there is no log listener yet — the log writer is what asks for this
        // directory in the first place.
        Assert.Contains("continuing to use the old path", AppPaths.MigrationNote);
    }
}
