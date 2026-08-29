using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The first run's own workspace: where it points, what it is called, and where the offer to create a
/// repository is withheld.
/// </summary>
/// <remarks>
/// Three rules that only make sense together. The seeded workspace sits at the user's home directory,
/// whose folder name is the login — so it needs a name of its own — and whose contents are every file
/// the user owns — so it must never be offered a <c>git init</c>. Splitting them across three files
/// would leave each one looking arbitrary.
/// </remarks>
public class DefaultWorkspaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-default-workspace").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a locked temp dir is not a failure */ }
        GC.SuppressFinalize(this);
    }

    private string WorkspacesFile => Path.Combine(_dir, "workspaces.json");
    private WorkspaceService NewWorkspaces() => new(WorkspacesFile);
    private PersistenceService NewLayouts() => new(Path.Combine(_dir, "layouts"));

    [Fact]
    public void A_first_run_opens_on_the_home_directory_with_one_terminal()
    {
        var workspaces = NewWorkspaces();
        var layouts = NewLayouts();

        DefaultWorkspace.SeedFirstRun(workspaces, layouts);

        var workspace = Assert.Single(workspaces.Workspaces);
        Assert.True(SpecialDirectories.IsHome(workspace.DirectoryPath));

        var root = layouts.LoadLayout(workspace.Id)?.RootTile;
        Assert.NotNull(root);
        Assert.True(root!.IsLeaf);
        Assert.Equal(TileContentType.Terminal, root.ContentType);
        Assert.False(string.IsNullOrEmpty(root.TileId));
        Assert.Null(root.First);
        Assert.Null(root.Second);
    }

    [Fact]
    public void A_list_that_already_has_workspaces_gains_nothing()
    {
        var workspaces = NewWorkspaces();
        workspaces.AddWorkspace(Path.Combine(_dir, "existing"), "Existing");

        DefaultWorkspace.SeedFirstRun(workspaces, NewLayouts());

        Assert.Equal(["Existing"], workspaces.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public void Seeding_twice_leaves_the_one_workspace_it_made()
    {
        // The application seeds on every launch, so the condition is the whole of the rule — a second
        // call must not add a second copy of the home directory.
        var workspaces = NewWorkspaces();
        var layouts = NewLayouts();

        DefaultWorkspace.SeedFirstRun(workspaces, layouts);
        DefaultWorkspace.SeedFirstRun(workspaces, layouts);

        Assert.Single(workspaces.Workspaces);
    }

    [Fact]
    public void A_workspace_the_user_removed_does_not_come_back_on_the_next_launch()
    {
        // Removing the last workspace leaves an empty list, which is the state a first run also has in
        // memory — so seeding on emptiness would put the removed workspace back every launch and make
        // the removal impossible to carry out.
        var layouts = NewLayouts();
        var first = NewWorkspaces();
        DefaultWorkspace.SeedFirstRun(first, layouts);
        first.RemoveWorkspace(Assert.Single(first.Workspaces).Id);

        var afterRestart = NewWorkspaces();
        DefaultWorkspace.SeedFirstRun(afterRestart, layouts);

        Assert.Empty(afterRestart.Workspaces);
    }

    [Fact]
    public void A_workspace_list_that_cannot_be_read_is_not_replaced_by_the_seed()
    {
        // A locked or truncated file loads as an empty list, and seeding writes: taken as a first run,
        // the user's whole list would be overwritten by the one workspace this creates.
        File.WriteAllText(WorkspacesFile, "{ this is not the workspace list");

        var workspaces = NewWorkspaces();
        DefaultWorkspace.SeedFirstRun(workspaces, NewLayouts());

        Assert.Empty(workspaces.Workspaces);
        Assert.Equal("{ this is not the workspace list", File.ReadAllText(WorkspacesFile));
    }

    [Fact]
    public void The_home_directory_is_shown_by_name_and_everything_else_by_its_own()
    {
        Assert.Equal(WorkspaceDisplayName.Home,
            WorkspaceDisplayName.For("andrz", SpecialDirectories.Home));
        Assert.Equal("mterminal", WorkspaceDisplayName.For("mterminal", _dir));
    }

    [Fact]
    public void The_displayed_name_does_not_change_what_is_stored()
    {
        var workspaces = NewWorkspaces();
        var workspace = workspaces.AddWorkspace(SpecialDirectories.Home, "andrz");

        Assert.Equal(WorkspaceDisplayName.Home, new WorkspaceItemViewModel(workspace).Name);
        Assert.Equal("andrz", workspace.Name);
    }

    [Fact]
    public void Only_the_home_directory_wears_the_home_glyph()
    {
        // The row says which directory it is twice — the name and the glyph — and both read the path,
        // so a folder the user happens to have called "Home" gets the word and not the mark.
        var home = new WorkspaceItemViewModel(new Workspace
        {
            Id = "home", Name = "andrz", DirectoryPath = SpecialDirectories.Home
        });
        var namesake = new WorkspaceItemViewModel(new Workspace
        {
            Id = "namesake", Name = "Home", DirectoryPath = _dir
        });

        Assert.True(home.IsHome);
        Assert.False(namesake.IsHome);
    }

    [Fact]
    public void The_home_directory_sorts_under_the_name_it_shows_and_not_the_login()
    {
        // "andrz" would put the row at the top of the list under a letter that appears nowhere on
        // screen. An alphabetical list whose order cannot be read is worth no more than an unsorted one.
        var rows = new List<WorkspaceItemViewModel>
        {
            Row("Golf", Path.Combine(_dir, "golf")),
            Row("andrz", SpecialDirectories.Home),
            Row("India", Path.Combine(_dir, "india"))
        };

        rows.Sort(WorkspaceDisplayOrder.Compare);

        Assert.Equal(["Golf", WorkspaceDisplayName.Home, "India"], rows.Select(r => r.Name));
    }

    [Fact]
    public void The_glyph_does_not_move_the_row()
    {
        // Wearing a house is a second way of saying what the name says; sorting on it as well would
        // bunch every aliased row at one end and override the alphabet the rest of the list is read by.
        // Pinning is the one thing that outranks the name, and it still does here.
        var home = Row("andrz", SpecialDirectories.Home);
        var pinned = Row("Zulu", Path.Combine(_dir, "zulu"), isFavorite: true);
        var rows = new List<WorkspaceItemViewModel> { home, pinned, Row("Alpha", Path.Combine(_dir, "alpha")) };

        rows.Sort(WorkspaceDisplayOrder.Compare);

        Assert.True(home.IsHome);
        Assert.Equal(["Zulu", "Alpha", WorkspaceDisplayName.Home], rows.Select(r => r.Name));
    }

    private static WorkspaceItemViewModel Row(string storedName, string path, bool isFavorite = false) =>
        new(new Workspace { Name = storedName, DirectoryPath = path, IsFavorite = isFavorite });

    [Fact]
    public void A_project_folder_is_offered_a_repository_and_the_home_directory_is_not()
    {
        Assert.True(SpecialDirectories.AllowsRepository(_dir));
        Assert.False(SpecialDirectories.AllowsRepository(SpecialDirectories.Home));
    }

    [Fact]
    public void A_directory_under_the_home_directory_is_still_offered_one()
    {
        // The rule is about the home directory itself, not about living under it — every checkout on a
        // normal machine is somewhere below it.
        Assert.True(SpecialDirectories.AllowsRepository(Path.Combine(SpecialDirectories.Home, "sources", "project")));
    }

    [Fact]
    public void A_trailing_separator_does_not_make_it_a_different_directory()
    {
        var home = SpecialDirectories.Home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.True(SpecialDirectories.IsHome(home + Path.DirectorySeparatorChar));
        Assert.True(SpecialDirectories.IsHome(Path.Combine(home, "sub", "..")));
    }

    [Fact]
    public void The_root_of_the_filesystem_is_not_offered_a_repository()
    {
        foreach (var root in Roots())
            Assert.False(SpecialDirectories.AllowsRepository(root), root);
    }

    /// <summary>The roots this machine can name, the process's own drive among them.</summary>
    /// <remarks>
    /// The working directory's root is the one that matters: a root was once recognised by normalizing
    /// it again, and <c>Path.GetFullPath("C:")</c> is the current directory on drive C: rather than
    /// <c>C:\</c> — so the rule failed for exactly the drive the process was running on, which in an
    /// installed copy is the system drive. A test using only the repository's own drive stayed green.
    /// </remarks>
    private IEnumerable<string> Roots()
    {
        foreach (var path in new[] { _dir, SpecialDirectories.Home, Environment.CurrentDirectory })
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            Assert.False(string.IsNullOrEmpty(root));
            yield return root!;
        }
    }

    [Fact]
    public void A_system_directory_and_anything_under_it_is_not_offered_a_repository()
    {
        var system = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/usr";

        Assert.False(SpecialDirectories.AllowsRepository(system));
        Assert.False(SpecialDirectories.AllowsRepository(Path.Combine(system, "share")));
    }

    [Fact]
    public void A_path_nothing_can_make_sense_of_is_not_offered_a_repository()
    {
        // "We cannot tell" is one of the answers, and it is not a yes: an empty DirectoryPath must not
        // put an offer to write to somewhere unknown on a row.
        Assert.False(SpecialDirectories.AllowsRepository(""));
        Assert.False(SpecialDirectories.AllowsRepository("   "));
    }
}
