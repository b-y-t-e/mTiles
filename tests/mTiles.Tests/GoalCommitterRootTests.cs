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
    [Trait("Category", "Slow")] // many real git processes; close to the budget on a Windows runner
    public async Task A_workspace_below_the_repository_root_still_commits_the_files_it_named()
    {
        using var repo = new GitTestRepo(prefix: "committer");
        repo.Write("README.md", "start\n");
        repo.CommitAll("initial");

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
}
