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
