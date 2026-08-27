using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The snapshot taken before a goal runs, pinned against a real repository.
/// </summary>
/// <remarks>
/// <para>These spawn git, which nothing else here does, and they earn it: every claim
/// <see cref="GoalBaseline"/> makes is a claim about what git does with a private index — that the
/// user's own index and working tree are untouched, that <c>HEAD</c> does not move, that untracked
/// files are captured. None of that can be established against a stub, and all of it is what stands
/// between a destroyed afternoon and one command.</para>
/// <para><b>git is required, not skipped past.</b> An earlier version of this returned early when git
/// was missing, which xunit 2 reports as a pass — a test that says nothing and says it in green, over
/// the one part of this tile that stands between a destroyed afternoon and one command. The premise was
/// wrong anyway: this repository cannot be checked out without git, so a machine running these without
/// it does not exist.</para>
/// </remarks>
public class GoalBaselineTests
{
    [Fact]
    public async Task It_captures_untracked_files_without_disturbing_the_repository()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("tracked.txt", "committed");
        repo.Git("add -A");
        repo.Git("commit -m first");

        // The three states that matter, and the second is the whole point: `git diff HEAD` cannot see
        // an untracked file and `git checkout HEAD -- path` cannot bring one back, so a new file is
        // exactly what is lost for ever without this.
        repo.Write("tracked.txt", "edited by the user");
        repo.Write("new.txt", "never committed");
        repo.Write("staged.txt", "staged by the user");
        repo.Git("add staged.txt");

        var headBefore = repo.Git("rev-parse HEAD").Trim();
        var statusBefore = repo.Git("status --porcelain");

        var result = await new GoalBaseline(repo.Path, "git").CaptureAsync("g1", default);

        Assert.False(result.NoRepository);
        var saved = Assert.IsType<string>(result.Ref);
        Assert.StartsWith(GoalBaseline.RefPrefix, saved);

        // Nothing moved. The user's staged file is still staged, their edit is still an edit, and the
        // branch is where they left it — which is what separates this from `git stash` and from the
        // commit aider makes.
        Assert.Equal(headBefore, repo.Git("rev-parse HEAD").Trim());
        Assert.Equal(statusBefore, repo.Git("status --porcelain"));
        Assert.Equal(headBefore, repo.Git($"rev-parse {saved}^").Trim());

        // Out of the way: nothing lists it, so nobody checks it out by accident and no ordinary push
        // sends it anywhere.
        Assert.DoesNotContain(saved, repo.Git("branch -a"));

        var contents = repo.Git($"ls-tree -r --name-only {saved}");
        Assert.Contains("new.txt", contents);
        Assert.Contains("staged.txt", contents);

        // And it is the *current* content that was captured, not what HEAD holds.
        Assert.Equal("edited by the user", repo.Git($"show {saved}:tracked.txt"));
    }

    /// <summary>
    /// The one failure the tile says anything about, because it is the one the user can act on.
    /// </summary>
    /// <remarks>
    /// A workspace with no repository has no way back from a deleted file at all — not even
    /// <c>git checkout HEAD</c>, which is what everybody reaches for. Every other failure here is a
    /// snapshot that did not happen somewhere git still works, and is logged rather than said.
    /// </remarks>
    [Fact]
    public async Task A_workspace_without_a_repository_says_so_rather_than_failing_quietly()
    {
        RequireGit();

        using var plain = new TempRepo(init: false);
        plain.Write("notes.txt", "no git here");

        var result = await new GoalBaseline(plain.Path, "git").CaptureAsync("g1", default);

        Assert.Null(result.Ref);
        Assert.True(result.NoRepository);
    }

    /// <summary>
    /// A repository nobody has committed in yet is the same answer, and for the same reason: there is
    /// no <c>HEAD</c> to parent a snapshot on, so there is nothing to recover from either.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_commits_is_treated_as_nowhere_to_snapshot()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("a.txt", "written before the first commit");

        var result = await new GoalBaseline(repo.Path, "git").CaptureAsync("g1", default);

        Assert.Null(result.Ref);
        Assert.True(result.NoRepository);
    }

    /// <summary>
    /// A broken <c>GitPath</c> is not a workspace without a repository, and must not be reported as
    /// one: the note about creating a repository would send the user to fix something that is not
    /// wrong, in a directory that already has a perfectly good <c>.git</c> in it.
    /// </summary>
    [Fact]
    public async Task A_git_that_cannot_be_run_is_not_reported_as_a_missing_repository()
    {
        using var repo = new TempRepo(init: false);

        var result = await new GoalBaseline(repo.Path, "git-that-does-not-exist").CaptureAsync("g1", default);

        Assert.Null(result.Ref);

        // NoRepository is decided by asking git again, and that question cannot be answered either —
        // so it answers "yes, a repository", which leaves the quieter of the two messages standing.
        // Saying nothing is the right failure for something the user cannot act on.
        Assert.False(result.NoRepository);
    }

    /// <summary>Fails loudly rather than passing quietly. See the note on this class.</summary>
    /// <summary>
    /// The commit path, end to end against a real repository — the scope, the awkward paths, and what
    /// it refuses to touch.
    /// </summary>
    /// <remarks>
    /// Every assertion here is one that shipped broken. The scope was read with rename detection on, so
    /// a renamed file's old path was never named and its deletion stayed in the tree for ever; the
    /// paths were read without <c>-z</c>, so anything outside ASCII came back as a quoted escape and
    /// was silently dropped; and the message went onto a command line, where a subject written by a
    /// model could hold a quote or a newline.
    /// </remarks>
    [Fact]
    public async Task It_commits_this_runs_work_and_nothing_else()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("ours.txt", "v1");
        repo.Write("theirs.txt", "v1");
        repo.Write("renamed-from.txt", "v1");
        repo.Git("add -A");
        repo.Git("commit -m first");

        // The user is already mid-change when the goal starts. This file is the one that must survive
        // untouched: a commit takes the whole file, so committing it would carry their work along.
        repo.Write("theirs.txt", "the user is in the middle of this");

        var baseline = await new GoalBaseline(repo.Path, "git").CaptureAsync("g1", default);
        var baselineRef = Assert.IsType<string>(baseline.Ref);

        // Now the run works: an edit, a new file with a name outside ASCII, a deletion by way of a
        // rename, and a file the user goes on editing beside it.
        repo.Write("ours.txt", "v2");
        repo.Write("żółw.txt", "a name git would quote");
        repo.Git("mv renamed-from.txt renamed-to.txt");

        // And the run touches the file the user was already in the middle of. That intersection is the
        // whole of LeftAlone: a file only *they* changed is not the run's business and is never
        // mentioned, while one they had both changed cannot be committed — `git commit -- path` takes
        // the whole file, so it would carry their half along under this run's message.
        repo.Write("theirs.txt", "the user's edit, plus a line the run added");

        var committer = new GoalCommitter(repo.Path, "git");
        var scope = await committer.ScopeAsync(baselineRef, default);

        Assert.Contains("ours.txt", scope.Files);
        Assert.Contains("żółw.txt", scope.Files);

        // Both halves of the rename, or the deletion is never committed and sits in the tree for ever.
        Assert.Contains("renamed-from.txt", scope.Files);
        Assert.Contains("renamed-to.txt", scope.Files);

        // Theirs is named, not committed.
        Assert.DoesNotContain("theirs.txt", scope.Files);
        Assert.Contains("theirs.txt", scope.LeftAlone);

        // A subject that would not survive a command line: a quote, a backslash, and two lines.
        var made = await committer.CommitAsync(
            [new GoalCommit
            {
                Type = "feat",
                Subject = "handle \"quoted\" paths \\ and\na second line",
                Files = [..scope.Files],
            }],
            default);

        Assert.Equal(1, made);
        Assert.Contains("handle \"quoted\" paths", repo.Git("log -1 --format=%s"));

        var committed = repo.Git("show --name-only --format= HEAD");
        Assert.Contains("ours.txt", committed);
        Assert.Contains("renamed-to.txt", committed);
        Assert.DoesNotContain("theirs.txt", committed);

        // The user's work is exactly where they left it.
        Assert.Equal("theirs.txt", repo.Git("status --porcelain")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l[3..].Trim())
            .Single());
    }

    private static void RequireGit() =>
        Assert.True(HasGit(), "git is not on PATH, so none of these can say anything about GoalBaseline.");

    private static bool HasGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; }

        public TempRepo(bool init = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"mtiles-baseline-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);

            if (!init) return;

            Git("init -q");
            // Set on the repository rather than relied on from the machine: a build agent has no global
            // identity, and `commit` would fail there for a reason that has nothing to do with what is
            // being tested. GoalBaseline passes its own identity for the same reason.
            Git("config user.name tester");
            Git("config user.email tester@localhost");
            Git("config commit.gpgsign false");
        }

        public void Write(string name, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), content);

        public string Git(string arguments)
        {
            using var p = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Dispose()
        {
            try
            {
                // Git leaves read-only files under .git/objects on Windows, which Directory.Delete
                // refuses. Clearing the attribute is cheaper than leaving a temp repository per test.
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Cleaning up the test repository failed: {ex.Message}");
            }
        }
    }
}
