using System.Diagnostics;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a scoped read of the working tree actually contains — against a real repository, because the
/// whole question is what git answers.
/// </summary>
/// <remarks>
/// <para>This pins the mechanism behind a blocker rather than the blocker itself. A goal's baseline is
/// a photograph of the working tree taken as the goal starts, and reading the tree "scoped" means
/// diffing against it: what has changed <em>since we started</em>. That is the right question for the
/// implement/review loop and the wrong one for <em>Detect &amp; run</em>, where the goal was written
/// from work that was already there — the baseline holds those very changes, so the diff is empty and
/// the review was handed nothing to judge.</para>
/// <para>Every other test around this stubs <c>WorktreeReader.Factory</c>, which bypasses the scoped
/// path entirely: the reader only asks git when there is no stub. So nothing exercised the code that
/// makes the two readings differ, and the difference is the bug.</para>
/// </remarks>
public class WorktreeScopeTests
{
    [Fact]
    public async Task A_baseline_taken_over_existing_work_hides_it_and_HEAD_shows_it()
    {
        Assert.True(HasGit(), "git is not on PATH, so this cannot say anything about WorktreeReader.");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        // The user's own uncommitted work — what "Detect & run" is asked to finish.
        repo.Write("cart.cs", "class Cart { int Total; }\n");

        var baseline = await new GoalBaseline(repo.Path, "git")
            .CaptureAsync("test", CancellationToken.None);
        Assert.NotNull(baseline.Ref);

        var reader = new WorktreeReader(repo.Path, "git");

        // Scoped to the baseline: nothing has happened since it was taken, so there is nothing here —
        // and this is exactly what the review used to be given on that path.
        var scoped = await reader.ReadAsync(CancellationToken.None, baselineRef: baseline.Ref);
        Assert.True(scoped.Readable);
        Assert.DoesNotContain("int Total", scoped.Text ?? "");

        // Against HEAD, which is what the same working tree looks like when the changes are the point.
        var whole = await reader.ReadAsync(CancellationToken.None);
        Assert.True(whole.Readable);
        Assert.Contains("int Total", whole.Text ?? "");
    }

    [Fact]
    public async Task Scoping_still_shows_what_changed_after_the_baseline_and_not_what_came_before()
    {
        Assert.True(HasGit(), "git is not on PATH, so this cannot say anything about WorktreeReader.");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        repo.Write("theirs.cs", "class Theirs { }\n");

        var baseline = await new GoalBaseline(repo.Path, "git")
            .CaptureAsync("test", CancellationToken.None);
        Assert.NotNull(baseline.Ref);

        // The tool's work, after the snapshot. A new file, which is the case a diff against HEAD
        // cannot show on its own and the reason the baseline is a tree rather than a commit range.
        repo.Write("ours.cs", "class Ours { }\n");

        var scoped = await new WorktreeReader(repo.Path, "git")
            .ReadAsync(CancellationToken.None, baselineRef: baseline.Ref);

        Assert.Contains("class Ours", scoped.Text ?? "");
        Assert.DoesNotContain("class Theirs", scoped.Text ?? "");
    }

    /// <summary>
    /// A file with no history reaches a detection with its contents, not as a bare name.
    /// </summary>
    /// <remarks>
    /// <para><c>ReadAsync</c> asks git two questions and the second — <c>ls-files --others</c> —
    /// answers with names. So a new file arrived as a path and nothing else, appeared in no
    /// <c>--stat</c> at all, and shared a cap of its own with every other new file. Measured on a real
    /// working tree, 2026-09-09: 102 tracked files changed and 44 untracked, of which the detection
    /// prompt carried seventeen names, no line counts and not one line of content — while the
    /// untracked half was the new work and therefore the goal.</para>
    /// <para>Against a real repository, because the whole claim is about what git answers when the
    /// working tree is written into a tree object through a private index.</para>
    /// </remarks>
    [Fact]
    public async Task A_whole_tree_read_carries_new_files_contents_where_the_ordinary_one_has_names()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        // The new work: a file git has never seen.
        repo.Write("discount.cs", "class Discount { const int Percent = 10; }\n");

        var reader = new WorktreeReader(repo.Path, "git");

        var names = await reader.ReadAsync(CancellationToken.None);
        Assert.Contains("discount.cs", names.Text ?? "");
        Assert.DoesNotContain("class Discount", names.Text ?? "");

        var whole = await reader.ReadWholeTreeAsync(CancellationToken.None);
        Assert.Contains("class Discount", whole.Text ?? "");

        // Everything uncommitted, not what one run changed: the prompts that warn about the user's own
        // parallel work go on warning.
        Assert.False(whole.Scoped);
        Assert.True(whole.Readable);
    }

    [Fact]
    public async Task A_whole_tree_read_that_cannot_be_taken_answers_as_the_ordinary_one_does()
    {
        RequiresGit.OrFail("WorktreeReader");

        // No repository at all, so there is no HEAD to write a tree against. The point is that this
        // degrades to the read it replaces rather than to an exception or to a tree that looks clean.
        var plain = Path.Combine(Path.GetTempPath(), $"mtiles-scope-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            File.WriteAllText(Path.Combine(plain, "notes.txt"), "no git here\n");

            var reader = new WorktreeReader(plain, "git");
            var whole = await reader.ReadWholeTreeAsync(CancellationToken.None);
            var ordinary = await reader.ReadAsync(CancellationToken.None);

            Assert.Equal(ordinary.Readable, whole.Readable);
            Assert.False(whole.Readable);
        }
        finally
        {
            try { Directory.Delete(plain, recursive: true); } catch { /* not a test failure */ }
        }
    }

    /// <summary>
    /// An <c>@</c> token that names a commit moves the end the tree is read from.
    /// </summary>
    /// <remarks>
    /// "Sprawdź ostatni commit" is the case this exists for: the change is committed, the tree is
    /// nearly clean, and everything the tile knew how to show was the handful of files still
    /// uncommitted. The token is not a path — <c>HEAD~1</c> carries neither a slash nor a dot — so the
    /// filesystem answers no and git answers yes, and neither answer is guessed at here.
    /// </remarks>
    [Fact]
    public async Task A_named_commit_becomes_the_end_the_tree_is_read_from()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        // The work that is already committed, and therefore invisible to every read against HEAD.
        repo.Write("discount.cs", "class Discount { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m discounts");

        // And one thing still uncommitted beside it.
        repo.Write("notes.md", "a note\n");

        var reader = new WorktreeReader(repo.Path, "git");

        var againstHead = await reader.ReadWholeTreeAsync(CancellationToken.None);
        Assert.Contains("notes.md", againstHead.Text ?? "");
        Assert.DoesNotContain("class Discount", againstHead.Text ?? "");

        var named = await GoalScopeRef.ResolveAsync(["HEAD~1"], repo.Path, "git", CancellationToken.None);
        Assert.NotNull(named);

        // The last commit *and* what is not committed yet, which is what somebody still working on it
        // is asking about.
        var since = await reader.ReadWholeTreeAsync(CancellationToken.None, readBase: named);
        Assert.Contains("class Discount", since.Text ?? "");
        Assert.Contains("notes.md", since.Text ?? "");
    }

    /// <summary>
    /// The commit is pinned when the scope is worked out, so a commit made afterwards does not move it.
    /// </summary>
    /// <remarks>
    /// <c>HEAD~1</c> is a relative name: committing in the terminal tile next door, or between closing
    /// the tile and pressing Resume tomorrow, makes it a different commit — and the run would then
    /// stop covering the very commit its goal was about, handing the reviewer a block with the work
    /// under judgement missing from it. Resolving to an id once is what makes the scope survive that.
    /// </remarks>
    [Fact]
    public async Task A_named_commit_is_pinned_and_does_not_move_when_the_user_commits()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");
        repo.Write("discount.cs", "class Discount { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m discounts");

        var named = await GoalScopeRef.ResolveAsync(["HEAD~1"], repo.Path, "git", CancellationToken.None);
        Assert.NotNull(named);
        Assert.Equal(repo.Git("rev-parse HEAD~1").Trim(), named!.Value.Base);

        // Somebody commits while the run is under way.
        repo.Write("shipping.cs", "class Shipping { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m shipping");

        // The commit the goal was about is still in the read, which is what a relative token would
        // have lost the moment HEAD moved.
        var since = await new WorktreeReader(repo.Path, "git")
            .ReadWholeTreeAsync(CancellationToken.None, readBase: named);
        Assert.Contains("class Discount", since.Text ?? "");

        // And the sentence the plan's prompt puts it in still says what the user typed.
        Assert.Equal("HEAD~1", named.Value.Spelling);
    }

    [Fact]
    public async Task A_named_range_compares_two_commits_and_leaves_the_working_tree_out()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");
        repo.Write("discount.cs", "class Discount { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m discounts");
        repo.Write("scratch.md", "not committed\n");

        var range = await GoalScopeRef.ResolveAsync(
            ["HEAD~1..HEAD"], repo.Path, "git", CancellationToken.None);
        Assert.NotNull(range);

        // Both ends come back as commit ids rather than as the words that named them, which is what
        // makes the range mean the same thing after somebody commits.
        Assert.NotNull(range!.Value.Head);
        Assert.Equal(repo.Git("rev-parse HEAD").Trim(), range.Value.Head);
        Assert.Equal(repo.Git("rev-parse HEAD~1").Trim(), range.Value.Base);
        Assert.Equal("HEAD~1", range.Value.Named);

        var read = await new WorktreeReader(repo.Path, "git")
            .ReadWholeTreeAsync(CancellationToken.None, readBase: range);

        Assert.Contains("class Discount", read.Text ?? "");
        Assert.DoesNotContain("scratch.md", read.Text ?? "");
    }

    /// <summary>
    /// A read that ends at the working tree keeps the base the user named and drops the head.
    /// </summary>
    /// <remarks>
    /// The rule the implement/review loop reads through: a pinned head end is two commits nothing the
    /// tool writes can move, so the same diff comes back every lap. Nothing else about the scope
    /// changes — a single ref already ends at the tree, and no ref at all still means none.
    /// </remarks>
    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("HEAD~1", null, "HEAD~1", null)]
    [InlineData("master", "HEAD", "master", null)]
    public void A_read_that_writes_ends_at_the_working_tree(
        string? baseRef, string? headRef, string? expectedBase, string? expectedHead)
    {
        GoalReadBase? scope = baseRef is null ? null : new GoalReadBase(baseRef, headRef);

        var ended = GoalScopeRef.EndingAtTheWorkingTree(scope);

        Assert.Equal(expectedBase, ended?.Base);
        Assert.Equal(expectedHead, ended?.Head);
    }

    [Fact]
    public async Task A_token_that_is_neither_a_file_nor_a_commit_changes_nothing()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        // "@admin about the failure" is prose with an at-sign in it, and an option-looking token is
        // refused before git ever sees it.
        Assert.Null(await GoalScopeRef.ResolveAsync(
            ["admin", "--hard", "nope/does/not/exist"], repo.Path, "git", CancellationToken.None));
    }

    /// <summary>
    /// A word that happens to be a branch name is still prose, and does not move the diff base.
    /// </summary>
    /// <remarks>
    /// The one shape where asking git alone answered yes to a sentence: "popraw formularz @admin" in a
    /// repository with an <c>admin</c> branch read the whole history since that branch as the changes
    /// that were just made. Naming the branch on purpose still works, as the range git already spells
    /// it: <c>@admin..</c>.
    /// </remarks>
    [Fact]
    public async Task A_word_that_is_also_a_branch_name_is_still_prose()
    {
        RequiresGit.OrFail("WorktreeReader");

        using var repo = new TempRepo();
        repo.Write("cart.cs", "class Cart { }\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");
        repo.Git("branch admin");

        Assert.Null(await GoalScopeRef.ResolveAsync(
            ["admin"], repo.Path, "git", CancellationToken.None));

        var named = await GoalScopeRef.ResolveAsync(
            ["admin.."], repo.Path, "git", CancellationToken.None);

        // The branch resolves, and what is kept is the commit it stood on — the branch itself will
        // move. What still says "admin" is the spelling the prompt reads it out by.
        Assert.Equal(repo.Git("rev-parse admin").Trim(), named?.Base);
        Assert.Equal("admin", named?.Spelling);
    }

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

        public TempRepo()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"mtiles-scope-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);

            Git("init -q");
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
