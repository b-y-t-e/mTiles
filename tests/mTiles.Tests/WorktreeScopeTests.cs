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
