using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public sealed class GitServiceStatusTests
{
    // A repository nobody has committed to yet is still a repository: the git tile read it as "Not a git
    // repository" and hid the very files the first commit is to be made of.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_repository_is_one_with_or_without_commits(bool committed)
    {
        using var repo = new GitTestRepo(prefix: "status");
        if (committed)
        {
            repo.Write("first.txt", "1");
            repo.CommitAll();
        }
        repo.Write("new.txt", "2");

        var status = await new GitService(repo.Path).GetStatusAsync();

        Assert.True(status.IsGitRepo);
        Assert.Equal(repo.Git("branch --show-current").Trim(), status.BranchName);
        Assert.Contains(status.Changes, c => c.FilePath == "new.txt");
        Assert.Equal(committed ? 1 : 0, status.CommitLog.Count);
    }
}
