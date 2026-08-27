using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Committing works when the workspace is a subdirectory of the repository.
/// </summary>
/// <remarks>
/// <para>Git treats its two ends differently and this is the seam between them: the paths it
/// <em>prints</em> — <c>diff --name-only</c>, which is where a commit plan's file list comes from — are
/// relative to the top of the repository, while the paths it is <em>given</em> as a pathspec are
/// relative to the current directory. Measured, not assumed: from a subdirectory, a diff names
/// <c>src/Views/Thing.axaml</c> and a pathspec spelled that way matches nothing.</para>
/// <para>So a committer running from the workspace was asking about files one directory too deep. The
/// visible failure is "pathspec did not match any file(s)", and the invisible one is worse: where the
/// same relative path happens to exist under the workspace, the wrong file is committed under a
/// message about this run. A monorepo whose workspace is one service is the ordinary case here, not a
/// corner.</para>
/// </remarks>
public class GoalCommitterRootTests
{
    [Fact]
    public async Task A_workspace_below_the_repository_root_still_commits_the_files_it_named()
    {
        Assert.True(HasGit(), "git is not on PATH, so this cannot say anything about GoalCommitter.");

        using var repo = new TempRepo();
        repo.Write("README.md", "start\n");
        repo.Git("add -A");
        repo.Git("commit -q -m initial");

        // The workspace is one directory down, and the file this run produced is new — which is what
        // most of an implementation produces, and the case that needs `add -N` before `commit --only`.
        Directory.CreateDirectory(Path.Combine(repo.Path, "app"));
        repo.Write(Path.Combine("app", "feature.txt"), "written by the run\n");

        var committer = new GoalCommitter(Path.Combine(repo.Path, "app"), "git");

        // The path as a diff would have named it: from the top of the repository.
        var made = await committer.CommitAsync(
            [new GoalCommit { Type = "feat", Subject = "a feature", Files = ["app/feature.txt"] }],
            CancellationToken.None);

        Assert.Equal(1, made);
        Assert.Contains("app/feature.txt", repo.Git("show --name-only --format= HEAD"));

        // And nothing was left staged behind it — the intent-to-add is either committed or taken back.
        Assert.Equal("", repo.Git("status --porcelain").Trim());
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
                System.IO.Path.GetTempPath(), $"mtiles-committer-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);

            Git("init -q");

            // On the repository rather than from the machine: a build agent has no global identity and
            // `commit` would fail there for a reason that has nothing to do with what is being tested.
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
