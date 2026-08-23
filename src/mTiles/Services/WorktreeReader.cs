using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// What the working tree currently looks like, as one block of text for a prompt.
/// <para>Its own class rather than three methods on the Goal tile's view model: reading a repository is
/// not the view model's job, and while it lived there every test that drove the loop spawned four git
/// processes a lap against whatever directory it happened to be given. <see cref="Factory"/> is the
/// seam that stops it — the same one the AI runner and the launch chain's PTY use.</para>
/// <para>The assembly of the pieces is in <see cref="GoalDiffContext"/> and stays pure; this class is
/// only the part that talks to git.</para>
/// </summary>
internal sealed class WorktreeReader(string workingDirectory, string gitPath)
{
    /// <summary>Replaced by a test. Null means read the real repository.</summary>
    internal static Func<string, CancellationToken, Task<string?>>? Factory { get; set; }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (Factory is { } stub)
            return await stub(workingDirectory, ct);

        try
        {
            var git = new GitCommandRunner(workingDirectory, gitPath);

            // Two commands, each allowed to fail on its own: `diff HEAD` fails in a repository with no
            // commits yet, and losing the untracked list to that would be losing the only part that
            // still had something to say.
            // The pathspec is on the diff too, not only on the listing. An untracked goal file cannot
            // appear in a diff — but a *committed* one can, and `.mterminal/` is only added to
            // .gitignore by the Git tile, so a workspace without one can easily have this tile's own
            // transcript under version control and its contents handed to the tool as "recent changes".
            var (diff, diffProblem) = await RunAsync(
                git, "diff HEAD -- \":(exclude).mterminal\"", ct);
            // `.mterminal` excluded by pathspec. Without a Git tile in the workspace nothing adds it to
            // .gitignore, so the listing included this tile's own state file — the transcript being
            // rewritten after every message — and handed the agent its path to tidy up or edit.
            var (untracked, untrackedProblem) = await RunAsync(
                git, "ls-files --others --exclude-standard -- \":(exclude).mterminal\"", ct);

            // Both problems, joined: reporting only the first hid the second, and the two commands fail
            // for different reasons.
            var problems = string.Join("; ", new[] { diffProblem, untrackedProblem }.Where(x => x != null));
            return GoalDiffContext.Compose(diff, untracked, problems);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Reading the working tree failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// One git command, whose failure is a warning and a line in the prompt rather than silence.
    /// <para>These used to run with <c>throwOnError: false</c>, which turned a broken git, a missing
    /// repository or a bad <c>GitPath</c> into an empty string — indistinguishable from a clean tree.
    /// The tool was then told nothing had changed, and nobody found out why.</para>
    /// </summary>
    private static async Task<(string Output, string? Problem)> RunAsync(
        GitCommandRunner git, string arguments, CancellationToken ct)
    {
        try
        {
            return (await git.RunAsync(arguments, ct), null);
        }
        // Rethrown rather than reported: a cancellation means the caller is going away, and answering
        // it with an empty tree would send a prompt claiming nothing had changed. The caller checks for
        // a pause straight afterwards, and this makes sure a cancellation without one cannot slip past
        // disguised as a clean worktree.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"git {arguments} failed: {ex.Message}");
            return ("", $"`git {arguments}` failed: {ex.Message}");
        }
    }
}
