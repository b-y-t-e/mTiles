using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A file whose name contains glob characters is committed, not matched as a pattern.
/// </summary>
/// <remarks>
/// A pathspec is a pattern unless it is told otherwise: <c>*</c>, <c>?</c> and <c>[...]</c> are magic,
/// so <c>Report[1].cs</c> matches <c>Report1.cs</c> and never itself. The commit then fails with
/// "did not match any file(s)" — in the middle of a plan, after earlier commits are already in the
/// user's history. <c>:(literal)</c> is git's own way of saying that the characters are the name.
/// </remarks>
public class GoalCommitterPathspecTests
{
    [Fact]
    public async Task A_name_full_of_glob_characters_is_committed_as_itself()
    {
        using var repo = new GitTestRepo(prefix: "pathspec");
        repo.Write("README.md", "start\n");
        repo.CommitAll("initial");

        // The decoy a glob would match instead, and the file that is actually named.
        repo.Write("Report1.cs", "// not this one\n");
        repo.Write("Report[1].cs", "// this one\n");

        var made = await new GoalCommitter(repo.Path, "git").CommitAsync(
            [new GoalCommit { Type = "feat", Subject = "a report", Files = ["Report[1].cs"] }],
            CancellationToken.None);

        Assert.Equal(1, made);

        var committed = repo.Git("show --name-only --format= HEAD");
        Assert.Contains("Report[1].cs", committed);
        Assert.DoesNotContain("Report1.cs", committed);
    }

    /// <summary>
    /// A commit naming more files than a command line can carry is still made.
    /// </summary>
    /// <remarks>
    /// Windows caps a command line at 32 767 characters. A plan can name hundreds of files — and the
    /// single sweeping commit this tile falls back to when the tool cannot answer names <em>all</em> of
    /// them — so past that cap <c>Process.Start</c> throws, in the middle of a plan whose earlier
    /// commits are already in the user's history. The pathspec goes in a file for that reason;
    /// splitting it is not available, because a commit is one act and two of them is a different answer
    /// from the one the user approved.
    /// </remarks>
    [Fact]
    [Trait("Category", "Slow")] // many real git processes; close to the budget on a Windows runner
    public async Task A_commit_naming_more_files_than_a_command_line_holds_is_still_made()
    {
        using var repo = new GitTestRepo(prefix: "pathspec");
        repo.Write("README.md", "start\n");
        repo.CommitAll("initial");

        // Long names on purpose: 400 of these is well past the cap once the quoting is counted.
        var files = Enumerable.Range(0, 400)
            .Select(i => $"src/a-rather-long-directory-name/and-another-one/generated-file-{i:0000}.cs")
            .ToList();

        Directory.CreateDirectory(
            Path.Combine(repo.Path, "src", "a-rather-long-directory-name", "and-another-one"));
        foreach (var file in files)
            File.WriteAllText(Path.Combine(repo.Path, file), "// generated\n");

        var made = await new GoalCommitter(repo.Path, "git").CommitAsync(
            [new GoalCommit { Type = "chore", Subject = "generated files", Files = [..files] }],
            CancellationToken.None);

        Assert.Equal(1, made);
        Assert.Equal(400, repo.Git("show --name-only --format= HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_failed_commit_leaves_the_users_own_staging_where_it_was()
    {
        using var repo = new GitTestRepo(prefix: "pathspec");
        repo.Write("theirs.cs", "class Theirs { }\n");
        repo.Write("ours.cs", "class Ours { }\n");
        repo.CommitAll("initial");

        // The user stages a change of their own while the run is going — to a file this run also
        // touched, which is the case that matters: `commit --only` ignores the index, so the staging
        // was never part of the commit and must survive its failure either way.
        repo.Write("ours.cs", "class Ours { int Staged; }\n");
        repo.Git("add ours.cs");

        // And the commit this run tries to make is rejected by a hook.
        var hook = Path.Combine(repo.Path, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");

        // Git runs a hook only if it is executable. Windows has no such bit and runs it anyway, so
        // without this the commit simply succeeded on Linux and the test asserted nothing at all.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);


        await Assert.ThrowsAsync<GoalCommitFailure>(() =>
            new GoalCommitter(repo.Path, "git").CommitAsync(
                [new GoalCommit { Type = "feat", Subject = "a thing", Files = ["ours.cs"] }],
                CancellationToken.None));

        // Still staged. `commit --only` ignores the index, so this staging was never part of the
        // commit — and the run takes back only the intent-to-add entries it made itself, of which a
        // tracked file needs none.
        Assert.Contains("M  ours.cs", repo.Git("status --porcelain"));
    }
}
