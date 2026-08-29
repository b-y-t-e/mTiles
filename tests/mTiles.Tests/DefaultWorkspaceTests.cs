using System.Globalization;
using Material.Icons;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
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
        // Both the name and the glyph read the path, so a folder the user happens to have named the
        // same gets the words and not the mark. The glyph now sits on the path line rather than in
        // front of the name, where it was a second spelling of a name that already says "Home
        // directory" — but which row wears it is unchanged, and this is that rule.
        //
        // Asserted on SpecialKind and on the glyph it is converted to, which is what the row draws.
        // It used to be asserted on a WorkspaceItemViewModel.IsHome that nothing else read: the test
        // passed, was named after the house, and would have gone on passing with no house anywhere.
        var home = new WorkspaceItemViewModel(new Workspace
        {
            Id = "home", Name = "andrz", DirectoryPath = SpecialDirectories.Home
        });
        var namesake = new WorkspaceItemViewModel(new Workspace
        {
            Id = "namesake", Name = WorkspaceDisplayName.Home, DirectoryPath = _dir
        });

        Assert.Equal(SpecialDirectoryKind.Home, home.SpecialKind);
        Assert.NotEqual(SpecialDirectoryKind.Home, namesake.SpecialKind);

        Assert.Equal(MaterialIconKind.Home, Glyph(home));
        Assert.NotEqual(MaterialIconKind.Home, Glyph(namesake));

        // And the line the glyph is on is shown for the one and not the other, once the check has
        // answered: an ordinary folder without a repository offers to make one, and that offer takes
        // the line. Both are told the same thing, so the path is the only difference between them.
        home.HasRepository = false;
        namesake.HasRepository = false;

        Assert.True(home.ShowsDirectoryPath);
        Assert.False(namesake.ShowsDirectoryPath);
    }

    /// <summary>The glyph the row's meta line draws, through the converter the markup uses.</summary>
    private static MaterialIconKind Glyph(WorkspaceItemViewModel row) =>
        (MaterialIconKind)SpecialDirectoryIcon.Kind.Convert(
            row.SpecialKind, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture)!;

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

        Assert.Equal(SpecialDirectoryKind.Home, home.SpecialKind);
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
    public void A_row_with_no_branch_and_no_offer_says_where_it_is()
    {
        // The one row whose meta line came out blank: no repository, and no offer to make one because
        // git init at the home directory is a mistake rather than a missing step. The line is reserved
        // on every row, so blank is height spent on silence — and the path is the fact the name does
        // not carry here, "Home directory" being an alias this application chose.
        var home = Row("andrz", SpecialDirectories.Home);
        var project = Row("mterminal", _dir);

        home.HasRepository = false;
        project.HasRepository = false;

        Assert.True(home.ShowsDirectoryPath);
        Assert.False(home.HasNoRepository);

        // Never both: a row that can be offered a repository is offered one, and the offer occupies
        // this line. The two are complements, not two things that might happen to agree.
        Assert.True(project.HasNoRepository);
        Assert.False(project.ShowsDirectoryPath);
    }

    [Fact]
    public void A_repository_says_its_branch_and_a_row_nobody_has_checked_says_nothing()
    {
        // The blank line has one honest cause left, and it has to stay blank: HasRepository is null
        // until the first pass answers, and a path shown there would be this row claiming to have been
        // checked. It is the same reason HasRepository is bool? at all.
        var unchecked_ = Row("andrz", SpecialDirectories.Home);
        Assert.False(unchecked_.ShowsDirectoryPath);
        Assert.False(unchecked_.HasNoRepository);

        // And a repository has a branch to put there instead, wherever it sits.
        var repository = Row("andrz", SpecialDirectories.Home);
        repository.HasRepository = true;
        Assert.False(repository.ShowsDirectoryPath);
    }

    [Fact]
    public void The_folders_the_system_made_for_the_users_own_files_are_named_one_by_one()
    {
        // Each gets its own answer rather than one "special" flag, because the row draws a glyph from
        // it and a disk, a house and a cog are three different pictures. Skipped where the platform
        // will not say — a blank answer must match nothing rather than everything.
        var folders = new (Environment.SpecialFolder Folder, SpecialDirectoryKind Kind)[]
        {
            (Environment.SpecialFolder.DesktopDirectory, SpecialDirectoryKind.Desktop),
            (Environment.SpecialFolder.MyDocuments, SpecialDirectoryKind.Documents),
            (Environment.SpecialFolder.MyPictures, SpecialDirectoryKind.Pictures),
            (Environment.SpecialFolder.MyMusic, SpecialDirectoryKind.Music),
            (Environment.SpecialFolder.MyVideos, SpecialDirectoryKind.Videos),
        };

        foreach (var (folder, kind) in folders)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0) continue;

            // On Unix .NET answers MyDocuments with the home directory itself — Personal is $HOME
            // there — so this loop asks about Home on Linux and Home is the right answer. Skipped
            // rather than asserted per platform: what is being pinned is that each folder the
            // platform names gets its own kind, and Home has its own test above. ~/Documents on Unix
            // is matched by name instead, which is the case below.
            if (SpecialDirectories.IsHome(path)) continue;

            Assert.Equal(kind, SpecialDirectories.Kind(path));
            Assert.False(SpecialDirectories.AllowsRepository(path), path);
        }
    }

    /// <summary>
    /// Downloads, and on Unix Documents, are found by name under the home directory.
    /// </summary>
    /// <remarks>
    /// The only two guesses here, and the ones easiest to break: neither is read from the platform —
    /// Downloads has no <c>SpecialFolder</c> at all, and the one Documents has answers with <c>$HOME</c>
    /// on Unix, which left <c>~/Documents</c> the single folder of the six offered a repository there.
    /// Nothing is created on disk, and nothing needs to be: <c>Kind</c> compares paths and never asks
    /// the filesystem, which is what lets it answer for a row whose directory has been moved away.
    /// </remarks>
    [Theory]
    [InlineData("Downloads", SpecialDirectoryKind.Downloads)]
    [InlineData("Documents", SpecialDirectoryKind.Documents)]
    public void The_folders_guessed_by_name_are_found_under_the_home_directory(
        string name, SpecialDirectoryKind kind)
    {
        var home = SpecialDirectories.Home;
        if (home.Length == 0) return;

        // Documents is a guess only where the platform would not say. On Windows it names the folder
        // itself — including a moved one, which a name would miss — so there is nothing to pin here.
        if (kind == SpecialDirectoryKind.Documents
            && !SpecialDirectories.IsHome(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))
            return;

        var path = Path.Combine(home, name);

        Assert.Equal(kind, SpecialDirectories.Kind(path));
        Assert.False(SpecialDirectories.AllowsRepository(path), path);

        // And only the folder itself, as with every other one of them.
        Assert.Equal(SpecialDirectoryKind.Ordinary, SpecialDirectories.Kind(Path.Combine(path, "a project")));
    }

    [Fact]
    public void A_drive_root_and_a_system_directory_say_which_they_are()
    {
        // AllowsRepository is tested above; this is the other half of the same answer, and the half
        // the row now draws its glyph from — a disk and a cog are two different pictures, and neither
        // is the folder both would fall to.
        foreach (var root in Roots())
            Assert.Equal(SpecialDirectoryKind.DriveRoot, SpecialDirectories.Kind(root));

        var system = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/usr";

        Assert.Equal(SpecialDirectoryKind.System, SpecialDirectories.Kind(system));
        Assert.Equal(SpecialDirectoryKind.System, SpecialDirectories.Kind(Path.Combine(system, "share")));
    }

    /// <summary>
    /// One picture per kind, and the two that have none share the folder.
    /// </summary>
    /// <remarks>
    /// The glyph is the whole of what the meta line says on these rows — the path beside it names the
    /// place, not the kind — so two kinds drawn the same is a row that cannot be read. The converter's
    /// <c>_</c> case is deliberate (a kind added and forgotten is drawn as a folder rather than
    /// throwing), which is exactly why nothing but the compiler would notice a duplicate: this is that
    /// notice.
    /// </remarks>
    [Fact]
    public void Every_kind_of_place_gets_its_own_glyph()
    {
        var drawn = Enum.GetValues<SpecialDirectoryKind>()
            .Where(k => k is not (SpecialDirectoryKind.Ordinary or SpecialDirectoryKind.Unknown))
            .Select(k => (Kind: k, Icon: SpecialDirectoryIcon.Kind.Convert(
                k, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture)))
            .ToList();

        Assert.Equal(drawn.Count, drawn.Select(d => d.Icon).Distinct().Count());

        // And the two without a picture fall to the folder rather than to whatever the first case is.
        foreach (var kind in new[] { SpecialDirectoryKind.Ordinary, SpecialDirectoryKind.Unknown })
            Assert.Equal(MaterialIconKind.FolderOutline, SpecialDirectoryIcon.Kind.Convert(
                kind, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_project_under_one_of_them_is_an_ordinary_project()
    {
        // The whole difference between these and the system directories: those match everything under
        // them, these match only themselves. A repository at ~/Documents tracks every file the user
        // ever put there; a repository at ~/Documents/thing is what people actually do.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0) return;

        var project = Path.Combine(documents, "a project");

        Assert.Equal(SpecialDirectoryKind.Ordinary, SpecialDirectories.Kind(project));
        Assert.True(SpecialDirectories.AllowsRepository(project));
    }

    [Fact]
    public void The_offer_is_withheld_for_exactly_the_paths_that_have_a_kind()
    {
        // One rule, one reading. AllowsRepository is derived from Kind rather than deciding again, so
        // the glyph on a row and the offer on it cannot come to different conclusions about the same
        // path — which is the failure two spellings of "is this the home directory" would produce.
        foreach (var path in new[]
                 {
                     _dir, SpecialDirectories.Home, Path.Combine(_dir, "project"), "", "   ",
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                 })
        {
            Assert.Equal(SpecialDirectories.Kind(path) == SpecialDirectoryKind.Ordinary,
                         SpecialDirectories.AllowsRepository(path));
        }
    }

    [Fact]
    public void A_path_nothing_can_make_sense_of_is_not_offered_a_repository()
    {
        // "We cannot tell" is one of the answers, and it is not a yes: an empty DirectoryPath must not
        // put an offer to write to somewhere unknown on a row.
        Assert.False(SpecialDirectories.AllowsRepository(""));
        Assert.False(SpecialDirectories.AllowsRepository("   "));

        // And it is its own answer, not a fall to "ordinary": the row draws a glyph from this, and
        // there is no true picture of a path nothing could read.
        Assert.Equal(SpecialDirectoryKind.Unknown, SpecialDirectories.Kind(""));
    }
}
