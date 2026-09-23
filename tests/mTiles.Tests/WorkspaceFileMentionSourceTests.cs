using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a workspace offers after an <c>@</c>: the walk's half when there is no git, and the same three
/// rules applied to git's own output when there is.
/// </summary>
/// <remarks>
/// What the walk does is ours — the separator it produces and what it leaves out — and both are wrong
/// silently. The walk tests share one read-only tree, built once for the class.
/// </remarks>
public class WorkspaceFileMentionSourceTests : IClassFixture<WorkspaceFileMentionSourceTests.WalkedTree>
{
    /// <summary>A tree with a file in a subfolder, hidden files, and build output at two depths — and the
    /// paths the walk offers for it, asked once.</summary>
    public sealed class WalkedTree : IDisposable
    {
        private readonly TempDirectory _dir = new("mtiles-mentions");

        public WalkedTree()
        {
            Directory.CreateDirectory(_dir["src/deep"]);
            Directory.CreateDirectory(_dir[".git"]);
            Directory.CreateDirectory(_dir["src/bin"]);
            Directory.CreateDirectory(_dir["node_modules"]);

            File.WriteAllText(_dir["README.md"], "");
            File.WriteAllText(_dir[".env"], "");
            File.WriteAllText(_dir["src/deep/Goal.cs"], "");
            File.WriteAllText(_dir[".git/HEAD"], "");
            File.WriteAllText(_dir["src/bin/mTiles.dll"], "");
            File.WriteAllText(_dir["node_modules/index.js"], "");

            // A path that cannot be run is the same answer as no git at all, and takes the walk.
            Paths = new WorkspaceFileMentionSource(_dir.Path, gitPath: "mtiles-no-such-git")
                .GetPathsAsync().GetAwaiter().GetResult().ToList();
        }

        public IReadOnlyList<string> Paths { get; }

        public void Dispose() => _dir.Dispose();
    }

    private readonly IReadOnlyList<string> _walked;

    public WorkspaceFileMentionSourceTests(WalkedTree tree) => _walked = tree.Paths;

    [Fact]
    public void Files_in_subfolders_come_back_with_forward_slashes()
    {
        Assert.Contains("src/deep/Goal.cs", _walked);
        Assert.Contains("README.md", _walked);
    }

    [Fact]
    public void Hidden_files_and_folders_are_left_out()
    {
        Assert.DoesNotContain(_walked, p => p.StartsWith(".git/", StringComparison.Ordinal));
        Assert.DoesNotContain(".env", _walked);
    }

    /// <summary>
    /// Build output is left out, wherever in the tree it sits, and brings no folder rows with it.
    /// </summary>
    /// <remarks>
    /// Not a matter of taste: the walk collects in directory order and stops at a ceiling, and those
    /// directories sort before <c>src</c> and outnumber it, so without this the whole list can be
    /// artefacts and the file the user is typing towards is not in it.
    /// </remarks>
    [Fact]
    public void Build_output_folders_are_left_out()
    {
        Assert.DoesNotContain("src/bin/mTiles.dll", _walked);
        Assert.DoesNotContain("node_modules/index.js", _walked);
        Assert.DoesNotContain("node_modules/", _walked);
        Assert.DoesNotContain("src/bin/", _walked);
        Assert.Contains("src/deep/Goal.cs", _walked);
    }

    /// <summary>
    /// A workspace that has gone missing is a mention that suggests nothing, not a tile that dies.
    /// </summary>
    [Fact]
    public async Task A_directory_that_is_not_there_offers_nothing()
    {
        var source = new WorkspaceFileMentionSource(
            Path.Combine(Path.GetTempPath(), "mtiles-mentions-gone", Guid.NewGuid().ToString("N")),
            gitPath: "mtiles-no-such-git");

        Assert.Empty(await source.GetPathsAsync());
    }

    /// <summary>
    /// Every folder above a file is offered too, once, ending in the separator, and before the files.
    /// </summary>
    /// <remarks>
    /// A mention is often a place rather than a file — <c>@src/deep/</c> says where to work — and the
    /// trailing slash is the only thing that tells a folder from a file anywhere downstream. Folders come
    /// first so that among rows the scorer likes equally the place sorts above what is inside it; the
    /// sort is stable, so this order is the tiebreak.
    /// </remarks>
    [Fact]
    public void Folders_are_offered_once_and_first_beside_the_files_in_them()
    {
        Assert.Single(_walked, path => path == "src/");
        Assert.Contains("src/deep/", _walked);
        Assert.Contains("src/deep/Goal.cs", _walked);
        Assert.True(_walked.ToList().IndexOf("src/") < _walked.ToList().IndexOf("README.md"));
    }

    // ── The three rules, applied to git's own output ────

    /// <summary>
    /// A real repository, so the git half is exercised rather than described: the excluded directories,
    /// the ignore files and the ceiling apply to what <b>git</b> lists, not only to the walk.
    /// </summary>
    /// <remarks>The build output is committed on purpose: git tracking it is what the exclusion has to
    /// overrule.</remarks>
    private static GitTestRepo Repository()
    {
        var repo = new GitTestRepo(prefix: "mentions");
        repo.Write("dist/bundle.js", "");
        repo.Write("obj/mTiles.dll", "");
        repo.Write("kept.cs", "");
        repo.Git("add -A -f");
        repo.Git("commit -q -m seed");
        return repo;
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
        using var repo = Repository();

        var paths = await new WorkspaceFileMentionSource(repo.Path).GetPathsAsync();

        Assert.Contains("kept.cs", paths);
        Assert.DoesNotContain("dist/bundle.js", paths);
        Assert.DoesNotContain("obj/mTiles.dll", paths);
        Assert.DoesNotContain("dist/", paths);
    }

    /// <summary>The workspace's own <c>.ignore</c> is honoured against tracked files too.</summary>
    [Fact]
    public async Task An_ignore_file_takes_a_tracked_file_off_the_list()
    {
        using var repo = Repository();

        File.WriteAllLines(Path.Combine(repo.Path, ".ignore"), ["kept.cs"]);

        var paths = await new WorkspaceFileMentionSource(repo.Path).GetPathsAsync();

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
        using var repo = Repository();

        File.WriteAllLines(Path.Combine(repo.Path, ".gitignore"), ["CLAUDE.md", ".claude/"]);
        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), "");
        Directory.CreateDirectory(Path.Combine(repo.Path, ".claude", "commands"));
        File.WriteAllText(Path.Combine(repo.Path, ".claude", "commands", "ship.md"), "");

        var paths = await new WorkspaceFileMentionSource(repo.Path).GetPathsAsync();

        Assert.Contains("CLAUDE.md", paths);
        Assert.Contains(".claude/commands/ship.md", paths);
    }

    /// <summary>An excluded name as the file's own name is a file, not a directory.</summary>
    [Fact]
    public async Task A_file_called_build_is_still_a_file()
    {
        using var repo = Repository();

        File.WriteAllText(Path.Combine(repo.Path, "build"), "");
        repo.Git("add -A -f");

        var paths = await new WorkspaceFileMentionSource(repo.Path).GetPathsAsync();

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
        using var repo = Repository();

        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), "");
        Directory.CreateDirectory(Path.Combine(repo.Path, ".claude", "commands"));
        File.WriteAllText(Path.Combine(repo.Path, ".claude", "commands", "ship.md"), "");

        var paths = await new WorkspaceFileMentionSource(repo.Path).GetPathsAsync();

        Assert.Single(paths, p => p == "CLAUDE.md");
        Assert.Single(paths, p => p == ".claude/commands/ship.md");
    }
}