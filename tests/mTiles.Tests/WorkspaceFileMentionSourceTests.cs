using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a workspace that is not a git repository offers, which is the walk's half of the source.
/// </summary>
/// <remarks>
/// The git half is not tested here: it is one <c>ls-files</c> call whose whole behaviour belongs to git,
/// and pinning it would mean creating a repository per test to assert that git can list files. What the
/// walk does is ours — the separator it produces and what it leaves out — and both are wrong silently.
/// </remarks>
public class WorkspaceFileMentionSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-mentions-" + Guid.NewGuid());

    public WorkspaceFileMentionSourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Directory.CreateDirectory(Path.Combine(_dir, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));

        File.WriteAllText(Path.Combine(_dir, "README.md"), "");
        File.WriteAllText(Path.Combine(_dir, ".env"), "");
        File.WriteAllText(Path.Combine(_dir, "src", "deep", "Goal.cs"), "");
        File.WriteAllText(Path.Combine(_dir, ".git", "HEAD"), "");
        File.WriteAllText(Path.Combine(_dir, "src", "bin", "mTiles.dll"), "");
        File.WriteAllText(Path.Combine(_dir, "node_modules", "index.js"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    /// <summary>A path that cannot be run is the same answer as no git at all, and takes the walk.</summary>
    private WorkspaceFileMentionSource Walking() => new(_dir, gitPath: "mtiles-no-such-git");

    [Fact]
    public async Task Files_in_subfolders_come_back_with_forward_slashes()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.Contains("src/deep/Goal.cs", paths);
        Assert.Contains("README.md", paths);
    }

    [Fact]
    public async Task Hidden_files_and_folders_are_left_out()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.DoesNotContain(paths, p => p.StartsWith(".git/", StringComparison.Ordinal));
        Assert.DoesNotContain(".env", paths);
    }

    /// <summary>
    /// Build output is left out, wherever in the tree it sits.
    /// </summary>
    /// <remarks>
    /// Not a matter of taste: the walk collects in directory order and stops at a ceiling, and those
    /// directories sort before <c>src</c> and outnumber it, so without this the whole list can be
    /// artefacts and the file the user is typing towards is not in it.
    /// </remarks>
    [Fact]
    public async Task Build_output_folders_are_left_out()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.DoesNotContain("src/bin/mTiles.dll", paths);
        Assert.DoesNotContain("node_modules/index.js", paths);
        Assert.Contains("src/deep/Goal.cs", paths);
    }

    /// <summary>
    /// A workspace that has gone missing is a mention that suggests nothing, not a tile that dies.
    /// </summary>
    [Fact]
    public async Task A_directory_that_is_not_there_offers_nothing()
    {
        var source = new WorkspaceFileMentionSource(
            Path.Combine(_dir, "gone"), gitPath: "mtiles-no-such-git");

        Assert.Empty(await source.GetPathsAsync());
    }

    /// <summary>
    /// Every folder above a file is offered too, each ending in the separator.
    /// </summary>
    /// <remarks>
    /// A mention is often a place rather than a file — <c>@src/deep/</c> says where to work — and the
    /// trailing slash is the only thing that tells a folder from a file anywhere downstream: the popup
    /// draws its row differently, and taking one steps into it rather than finishing the mention.
    /// </remarks>
    [Fact]
    public async Task Folders_are_offered_beside_the_files_in_them()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.Contains("src/", paths);
        Assert.Contains("src/deep/", paths);
        Assert.Contains("src/deep/Goal.cs", paths);
    }

    /// <summary>A folder is listed once however many files are under it.</summary>
    [Fact]
    public async Task A_folder_is_offered_once()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.Single(paths, path => path == "src/");
    }

    /// <summary>
    /// Folders come before files, so that among rows the scorer likes equally the place sorts above
    /// what is inside it. The sort is stable, so this order is the tiebreak.
    /// </summary>
    [Fact]
    public async Task Folders_come_first()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.True(paths.ToList().IndexOf("src/") < paths.ToList().IndexOf("README.md"));
    }

    /// <summary>A folder nobody skipped is not conjured out of one that was.</summary>
    [Fact]
    public async Task Skipped_folders_bring_no_folder_rows_with_them()
    {
        var paths = await Walking().GetPathsAsync();

        Assert.DoesNotContain("node_modules/", paths);
        Assert.DoesNotContain("src/bin/", paths);
    }

    // ── The three rules, applied to git's own output ────

    /// <summary>
    /// A real repository, so the git half is exercised rather than described.
    /// </summary>
    /// <remarks>
    /// The whole point of the arrangement is that the excluded directories, the
    /// ignore files and the ceiling apply to what <b>git</b> lists, not only to the walk — so a test
    /// that never runs git cannot see any of it. Skipped where git is not installed rather than failed:
    /// that is the machine's answer, not the code's.
    /// </remarks>
    private bool MakeRepository()
    {
        // A machine without git skips these; a machine with git must not, or the tests that carry the
        // whole point of the arrangement would pass by never running.
        if (!GitIsInstalled) return false;

        // The walk's fixture puts an empty `.git/HEAD` there to prove hidden directories are skipped.
        // Left standing it is a corrupt repository, and `git init` over it produced one that could not
        // commit — which the tests below answered by returning early and passing without running.
        Directory.Delete(Path.Combine(_dir, ".git"), recursive: true);

        if (!Run("init") || !Run("config user.email a@b.c") || !Run("config user.name t")) return false;

        Directory.CreateDirectory(Path.Combine(_dir, "dist"));
        Directory.CreateDirectory(Path.Combine(_dir, "obj"));
        File.WriteAllText(Path.Combine(_dir, "dist", "bundle.js"), "");
        File.WriteAllText(Path.Combine(_dir, "obj", "mTiles.dll"), "");
        File.WriteAllText(Path.Combine(_dir, "kept.cs"), "");

        // Committed on purpose: git tracking them is what the exclusion has to overrule.
        return Run("add -A -f") && Run("commit -m seed");
    }

    /// <summary>Whether this machine has git at all, asked once.</summary>
    private static readonly bool GitIsInstalled = HasGit();

    private static bool HasGit()
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;

            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The repository has to be buildable wherever git is, or a skip is hiding a failure.</summary>
    [Fact]
    public void Where_there_is_git_these_tests_actually_run() =>
        Assert.Equal(GitIsInstalled, MakeRepository());

    private bool Run(string arguments)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = _dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;

            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A tracked <c>dist/</c> or <c>obj/</c> is still not offered.
    /// </summary>
    /// <remarks>
    /// The excluded-directory check runs over git's output as well as the walk's, and this
    /// is the one place the popup overrules the user's own <c>.gitignore</c>: committing a bundle says
    /// something about distributing it and nothing about wanting it in a list of suggestions.
    /// </remarks>
    [Fact]
    public async Task A_tracked_build_directory_is_still_not_offered()
    {
        if (!MakeRepository()) return;

        var paths = await new WorkspaceFileMentionSource(_dir).GetPathsAsync();

        Assert.Contains("kept.cs", paths);
        Assert.DoesNotContain("dist/bundle.js", paths);
        Assert.DoesNotContain("obj/mTiles.dll", paths);
        Assert.DoesNotContain("dist/", paths);
    }

    /// <summary>The workspace's own <c>.ignore</c> is honoured against tracked files too.</summary>
    [Fact]
    public async Task An_ignore_file_takes_a_tracked_file_off_the_list()
    {
        if (!MakeRepository()) return;

        File.WriteAllLines(Path.Combine(_dir, ".ignore"), ["kept.cs"]);

        var paths = await new WorkspaceFileMentionSource(_dir).GetPathsAsync();

        Assert.DoesNotContain("kept.cs", paths);
    }

    /// <summary>
    /// The agent's own configuration is offered even where git refuses to see it.
    /// </summary>
    /// <remarks>
    /// The reason is visible in almost any repository's <c>.gitignore</c>, which routinely carries <c>CLAUDE.md</c> and <c>/.claude</c>: the files telling the
    /// agent how to work are routinely the ones the repository declines to track, and they are exactly
    /// what somebody writing a goal points at.
    /// </remarks>
    [Fact]
    public async Task The_agents_own_configuration_is_offered_though_git_ignores_it()
    {
        if (!MakeRepository()) return;

        File.WriteAllLines(Path.Combine(_dir, ".gitignore"), ["CLAUDE.md", ".claude/"]);
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "");
        Directory.CreateDirectory(Path.Combine(_dir, ".claude", "commands"));
        File.WriteAllText(Path.Combine(_dir, ".claude", "commands", "ship.md"), "");

        var paths = await new WorkspaceFileMentionSource(_dir).GetPathsAsync();

        Assert.Contains("CLAUDE.md", paths);
        Assert.Contains(".claude/commands/ship.md", paths);
    }

    /// <summary>An excluded name as the file's own name is a file, not a directory.</summary>
    [Fact]
    public async Task A_file_called_build_is_still_a_file()
    {
        if (!MakeRepository()) return;

        File.WriteAllText(Path.Combine(_dir, "build"), "");
        Run("add -A -f");

        var paths = await new WorkspaceFileMentionSource(_dir).GetPathsAsync();

        Assert.Contains("build", paths);
    }

    /// <summary>
    /// The agent's own configuration is offered once, even when git also lists it.
    /// </summary>
    /// <remarks>
    /// It is added outside the git listing, so a <c>CLAUDE.md</c> that is untracked and <em>not</em>
    /// ignored arrives twice — once from here and once from <c>ls-files --others</c>. A repository
    /// before its first commit has every one of these files in exactly that state, so the handful of
    /// paths this feature exists to surface each took two of the fifteen rows a popup has. The existing
    /// test uses an ignored file and therefore never met this.
    /// </remarks>
    [Fact]
    public async Task An_untracked_configuration_file_is_offered_once()
    {
        if (!MakeRepository()) return;

        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "");
        Directory.CreateDirectory(Path.Combine(_dir, ".claude", "commands"));
        File.WriteAllText(Path.Combine(_dir, ".claude", "commands", "ship.md"), "");

        var paths = await new WorkspaceFileMentionSource(_dir).GetPathsAsync();

        Assert.Single(paths, p => p == "CLAUDE.md");
        Assert.Single(paths, p => p == ".claude/commands/ship.md");
    }
}