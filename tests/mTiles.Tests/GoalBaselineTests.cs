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

        // No closing snapshot, which is a goal file written before this tile recorded one — the
        // scope then reaches up to the tree as it stands and reports that it is not bounded.
        var committer = new GoalCommitter(repo.Path, "git");
        var scope = await committer.ScopeAsync(baselineRef, null, default);

        Assert.False(scope.Bounded);

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

    /// <summary>
    /// One Goal tile does not commit another Goal tile's work.
    /// </summary>
    /// <remarks>
    /// <para>The failure this was written for: three Goal tiles in one workspace, all three finished,
    /// Commit pressed in the first — and all three runs went into the history under the first one's
    /// messages. A baseline is only the lower end of a run, so "what this run changed" meant
    /// "everything that has changed since it started", which is every tile that finished afterwards.
    /// </para>
    /// <para>Two files, because the two halves fail differently. A file only the second tile wrote is
    /// not the first tile's at all and must not appear anywhere in its scope; a file <em>both</em>
    /// wrote cannot be committed by the first either — <c>git commit -- path</c> takes the whole file
    /// — but it is named rather than dropped, because a file this run really did write and will not
    /// commit is something the user has to be told about.</para>
    /// </remarks>
    [Fact]
    public async Task One_goal_tile_does_not_commit_anothers_work()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("shared.txt", "v1");
        repo.Git("add -A");
        repo.Git("commit -m first");

        var git = new GoalBaseline(repo.Path, "git");

        // The first tile runs and finishes.
        var first = Assert.IsType<string>((await git.CaptureAsync("g1", default)).Ref);
        repo.Write("first.txt", "written by the first tile");
        repo.Write("shared.txt", "v2, by the first tile");
        var firstEnd = Assert.IsType<string>((await git.CaptureEndAsync("g1", default)).Ref);

        // The second tile runs and finishes afterwards, which is what the first one used to claim.
        await git.CaptureAsync("g2", default);
        repo.Write("second.txt", "written by the second tile");
        repo.Write("shared.txt", "v3, by the second tile");
        await git.CaptureEndAsync("g2", default);

        var committer = new GoalCommitter(repo.Path, "git");
        var scope = await committer.ScopeAsync(first, firstEnd, default);

        Assert.True(scope.Bounded);
        Assert.Equal(["first.txt"], scope.Files);

        // Never the second tile's own file, and not mentioned either: the first run never wrote it,
        // so there is nothing to explain about it.
        Assert.DoesNotContain("second.txt", scope.Files);
        Assert.DoesNotContain("second.txt", scope.LeftAlone);
        Assert.DoesNotContain("second.txt", scope.TouchedSince);

        // The file both wrote is held back and named.
        Assert.DoesNotContain("shared.txt", scope.Files);
        Assert.Equal(["shared.txt"], scope.TouchedSince);
        Assert.Empty(scope.LeftAlone);

        var made = await committer.CommitAsync(
            [new GoalCommit { Type = "feat", Subject = "the first tile", Files = [..scope.Files] }],
            default);

        Assert.Equal(1, made);

        var committed = repo.Git("show --name-only --format= HEAD");
        Assert.Contains("first.txt", committed);
        Assert.DoesNotContain("second.txt", committed);
        Assert.DoesNotContain("shared.txt", committed);

        // And the second tile still has everything it produced, waiting for its own Commit.
        var left = repo.Git("status --porcelain")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l[3..].Trim())
            .ToList();
        Assert.Contains("second.txt", left);
        Assert.Contains("shared.txt", left);
    }

    /// <summary>
    /// A run that finished before anybody else moved commits everything it wrote.
    /// </summary>
    /// <remarks>
    /// The other side of the test above, and the one that would catch a boundary drawn too tightly: a
    /// single tile in a quiet workspace must still commit its whole run, closing snapshot or not.
    /// </remarks>
    [Fact]
    public async Task A_run_nobody_disturbed_commits_all_of_its_own_work()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("start.txt", "v1");
        repo.Git("add -A");
        repo.Git("commit -m first");

        var git = new GoalBaseline(repo.Path, "git");
        var baseline = Assert.IsType<string>((await git.CaptureAsync("g1", default)).Ref);

        repo.Write("start.txt", "v2");
        repo.Write("added.txt", "new");

        var end = Assert.IsType<string>((await git.CaptureEndAsync("g1", default)).Ref);

        var scope = await new GoalCommitter(repo.Path, "git").ScopeAsync(baseline, end, default);

        Assert.True(scope.Bounded);
        Assert.Equal(["added.txt", "start.txt"], scope.Files.OrderBy(f => f).ToList());
        Assert.Empty(scope.TouchedSince);
        Assert.Empty(scope.LeftAlone);
    }

    /// <summary>Fails loudly rather than passing quietly. See the note on this class.</summary>
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

    /// <summary>
    /// A closing snapshot git no longer has degrades the scope, it does not block the commit.
    /// </summary>
    /// <remarks>
    /// <para>These refs are pruned to the newest twenty of their namespace and a tile keeps its
    /// <c>EndRef</c> for as long as the tile exists, so a goal reopened after twenty later summaries in
    /// the same workspace holds a ref that has been collected. Used unchecked, the first <c>diff</c>
    /// against it fails and the whole scope comes back <c>Unreadable</c> — the dialog says git could
    /// not be asked, and a run that committed perfectly well before closing snapshots existed becomes
    /// one that cannot be committed <em>because</em> of them.</para>
    /// <para>A missing upper end is exactly what <c>Bounded: false</c> already describes and the dialog
    /// already explains, so that is where it lands.</para>
    /// </remarks>
    [Fact]
    public async Task A_closing_snapshot_that_has_been_collected_falls_back_to_an_unbounded_scope()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("start.txt", "v1");
        repo.Git("add -A");
        repo.Git("commit -m first");

        var git = new GoalBaseline(repo.Path, "git");
        var baseline = Assert.IsType<string>((await git.CaptureAsync("g1", default)).Ref);

        repo.Write("start.txt", "v2");

        var end = Assert.IsType<string>((await git.CaptureEndAsync("g1", default)).Ref);

        // What the prune does to an older tile's end while its state file still names it.
        repo.Git($"update-ref -d {end}");

        var scope = await new GoalCommitter(repo.Path, "git").ScopeAsync(baseline, end, default);

        Assert.True(scope.Readable, "a collected end ref made the whole scope unreadable");
        Assert.False(scope.Bounded);
        Assert.Contains("start.txt", scope.Files);
    }

    /// <summary>
    /// Two runs that overlap in time are <b>not</b> told apart, and this is what that costs.
    /// </summary>
    /// <remarks>
    /// <para>The sequential case is pinned by
    /// <see cref="One_goal_tile_does_not_commit_anothers_work"/> and it holds. This is the other
    /// arrangement, and it does not: every question <c>GoalCommitter</c> asks is about <em>when</em> a
    /// file changed and none is about <em>who</em> changed it. B's file lands inside A's
    /// baseline-to-end window, so it is in what A changed; it is uncommitted, so it survives that
    /// filter; and it is in neither held-back list, because <c>LeftAlone</c> covers only what was dirty
    /// before A started and <c>TouchedSince</c> only what moved after A finished.</para>
    /// <para><b>Asserted rather than fixed</b>, and deliberately: this is a limit named in
    /// <c>docs/GOAL.md</c>, not a bug with a small correction behind it — closing it means recording
    /// which files a run's own attempts wrote instead of bracketing them in time. The test exists so
    /// that the day somebody does close it, this stops passing and says so, and so that nobody reads
    /// the sequential test as covering both.</para>
    /// <para>The sharper half is the last assertion: the scope comes back <c>Bounded</c>, so the dialog
    /// stays silent about another tile's work being indistinguishable. The wrong set of files arrives
    /// with no warning attached to it.</para>
    /// </remarks>
    [Fact]
    public async Task Two_runs_that_overlap_in_time_are_not_told_apart()
    {
        RequireGit();

        using var repo = new TempRepo();
        repo.Write("start.txt", "v1");
        repo.Git("add -A");
        repo.Git("commit -m first");

        var git = new GoalBaseline(repo.Path, "git");

        // A starts.
        var baselineA = Assert.IsType<string>((await git.CaptureAsync("a", default)).Ref);

        // B starts, works and finishes — entirely inside A's run.
        await git.CaptureAsync("b", default);
        repo.Write("written-by-b.txt", "B's work");
        await git.CaptureEndAsync("b", default);

        // A writes its own file and finishes after B.
        repo.Write("written-by-a.txt", "A's work");
        var endA = Assert.IsType<string>((await git.CaptureEndAsync("a", default)).Ref);

        var scope = await new GoalCommitter(repo.Path, "git").ScopeAsync(baselineA, endA, default);

        Assert.Contains("written-by-a.txt", scope.Files);

        // The limitation, stated as an assertion so it cannot be believed away.
        Assert.Contains("written-by-b.txt", scope.Files);
        Assert.Empty(scope.LeftAlone);
        Assert.Empty(scope.TouchedSince);

        // And nothing warns, because as far as the boundaries are concerned the run is well bounded.
        Assert.True(scope.Bounded);
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
