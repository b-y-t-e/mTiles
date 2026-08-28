using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// What a baseline attempt came back with: the ref that now holds the tree, or why there is none.
/// </summary>
/// <param name="Ref">The ref the snapshot was stored under, or null when none was taken.</param>
/// <param name="NoRepository">
/// True when there is nothing here to snapshot into — the workspace is not a git repository, or is one
/// with no commit yet.
/// <para>Its own field rather than a message to match on, because it is the one outcome the tile says
/// something about. Every other failure is a snapshot that did not happen in a workspace that still has
/// git under it; this one is a workspace where <b>nothing</b> can undo what the tool does, which the
/// user can act on — the workspaces panel has offered <em>Create repository</em> all along.</para>
/// </param>
internal readonly record struct GoalBaselineResult(string? Ref, bool NoRepository)
{
    public static readonly GoalBaselineResult None = new(null, false);
    public static readonly GoalBaselineResult NotARepository = new(null, true);
}

/// <summary>
/// A photograph of the working tree taken before a goal starts, so that work the tool destroys can be
/// got back.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The review is handed the whole of <c>git diff HEAD</c> under the
/// heading "the changes that were just made", which is a claim this tile cannot support: the user works
/// in the terminal tiles next door while a goal runs. A reviewer shown somebody else's parallel change
/// reports it — <em>unrelated changes glued onto this one</em> — and the next attempt does the only
/// thing that makes such a finding go away. It reverted the user's files and deleted the ones they had
/// not committed. <c>GoalPromptBuilder.OtherPeoplesWork</c> tells the tool not to; this is what is left
/// when it does anyway, and it is needed because a prompt is a request. The same failure is on record
/// against other agents, with an instruction in <c>AGENTS.md</c> saying not to.</para>
/// <para><b>Why it does not use <c>git stash</c>, and does not commit.</b> A stash <em>takes</em> the
/// changes out of the working tree, so the tool would arrive to a clean one and write the work again
/// beside it. A commit moves <c>HEAD</c> and leaves something in the user's history that they have to
/// undo — aider does exactly that, and can, because it commits after every edit anyway. This tile
/// commits nothing and is not going to start.</para>
/// <para><b>What it does instead.</b> The index is git's staging area, one file at
/// <c>&lt;git-dir&gt;/index</c>. Point <c>GIT_INDEX_FILE</c> at a copy and every <c>git add</c> writes
/// to the copy, so the user's own staged work is untouched — and, measured, <c>.git/index.lock</c> is
/// never taken, so a rebase in the terminal tile next door cannot collide with this. From that copy,
/// <c>write-tree</c> makes a tree and <c>commit-tree</c> makes a commit object <em>beside</em> the
/// history rather than in it: no branch moves, nothing appears in <c>git log</c>, <c>git branch</c> or
/// <c>git status</c>. A ref under <c>refs/mtiles/</c> is what stops <c>git gc</c> collecting it, and it
/// is out of <c>refs/heads</c> and <c>refs/tags</c>, so it is not pushed by <c>push</c>, <c>--all</c>
/// or <c>--tags</c>.</para>
/// <para><b>Untracked files are the point.</b> <c>git diff HEAD</c> does not show them and
/// <c>git checkout HEAD -- path</c> cannot bring one back, because it was never in HEAD — and a new
/// file is most of what an implementation produces. <c>add -A</c> puts them in the snapshot, which is
/// the difference between a recoverable incident and a permanent loss.</para>
/// <para><b>Everything here fails soft</b>, the rule <see cref="AppPaths"/> and
/// <see cref="WorkspacePaths"/> follow. A snapshot that cannot be taken is a goal that runs without
/// one; it is never a goal that does not start.</para>
/// </remarks>
internal sealed class GoalBaseline(string workingDirectory, string gitPath)
{
    /// <summary>Replaced by a test. Returns the ref to pretend was written, or an empty string for a
    /// workspace with no repository in it.</summary>
    internal static Func<string, CancellationToken, Task<GoalBaselineResult>>? Factory { get; set; }

    /// <summary>Where the snapshots live. Deliberately not under <c>refs/heads</c> or
    /// <c>refs/tags</c>: nothing lists them, nothing checks them out by accident, and no ordinary push
    /// sends them anywhere. (<c>git push --mirror</c> does, which is worth knowing and not worth
    /// designing around.)</summary>
    public const string RefPrefix = "refs/mtiles/goals/";

    /// <summary>
    /// Where the <em>closing</em> snapshots live — the tree as it stood when a run finished.
    /// </summary>
    /// <remarks>
    /// <para>A namespace of its own rather than a differently named ref under
    /// <see cref="RefPrefix"/>, and that is the prune talking. <see cref="PruneAsync"/> keeps the
    /// newest <see cref="Keep"/> of whatever prefix it is given: sharing one would mean every run
    /// wrote two refs into a window sized for one, so twenty runs of history became ten — and the ref
    /// evicted first is always a baseline, which is the one somebody's lost afternoon is recovered
    /// from.</para>
    /// </remarks>
    public const string EndRefPrefix = "refs/mtiles/ends/";

    /// <summary>
    /// How long the snapshot may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <c>git add -A</c> hashes the contents of everything not ignored, so a workspace whose build
    /// output is untracked <em>and</em> unignored can turn this into hundreds of megabytes of writing.
    /// Ten seconds is far more than a repository with a working <c>.gitignore</c> needs — measured at
    /// 0.14s here — and it bounds the case where somebody's <c>node_modules</c> is not ignored, which
    /// would otherwise stall the start of the goal with nothing on screen explaining it.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many snapshots are kept per workspace.
    /// </summary>
    /// <remarks>
    /// The one place this tile prunes anything, and the exception is argued rather than assumed. Goal
    /// files are kept for ever because they are kilobytes and nothing distinguishes a closed tile from
    /// a closed workspace. These are not kilobytes: each holds a blob for every file that differed from
    /// HEAD, in a repository that belongs to somebody else. Twenty is far more history than the
    /// question "what did I lose in the last run" ever reaches back for.
    /// </remarks>
    private const int Keep = 20;

    /// <summary>
    /// Takes the snapshot, and answers with the ref holding it.
    /// </summary>
    /// <param name="goalId">The goal's own id, which becomes the last part of the ref so the user can
    /// tell one run's snapshot from another's.</param>
    public Task<GoalBaselineResult> CaptureAsync(string goalId, CancellationToken ct) =>
        CaptureAsync(goalId, RefPrefix, "working tree before goal", ct);

    /// <summary>
    /// Takes the closing snapshot: the working tree as it stands now that this run has finished.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what makes a commit honest about a workspace with more than one Goal tile in
    /// it.</b> A baseline alone gives the run a lower bound, so "what this run changed" was read as
    /// "everything that has changed since it started" — and a second tile finishing afterwards is
    /// exactly that. Three tiles run in one workspace, the first one's Commit was pressed, and it
    /// committed all three runs' work under the first one's messages.</para>
    /// <para>Taken when the run reaches its summary, so the pair of trees brackets the run itself:
    /// what changed between them is this run's, and what changed after them is somebody else's — the
    /// other tile, or the user, and <see cref="GoalCommitter"/> treats those the same way because a
    /// commit takes the whole file either way.</para>
    /// <para>It is written and kept exactly as the baseline is, for the same reason: a tile is
    /// reopened days later and offers to commit, long after a dangling tree would have been collected.
    /// </para>
    /// </remarks>
    public Task<GoalBaselineResult> CaptureEndAsync(string goalId, CancellationToken ct) =>
        CaptureAsync(goalId, EndRefPrefix, "working tree after goal", ct);

    private async Task<GoalBaselineResult> CaptureAsync(
        string goalId, string prefix, string what, CancellationToken ct)
    {
        if (Factory is { } stub) return await stub(workingDirectory, ct);

        // The id goes into a ref name and into a commit message, both of which are built into a single
        // command-line string. Today's callers pass a hex guid and a timestamp, so nothing can go wrong
        // — which is exactly the kind of safety that stops being true when somebody changes what a goal
        // file is called. Making it true here rather than by the caller's habit costs one line.
        goalId = Safe(goalId);

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        try
        {
            var git = new GitCommandRunner(workingDirectory, gitPath);

            // Asked, not inferred from an exception. These fail the same way a missing git binary does
            // — a non-zero exit — and the two answers call for different things: "there is nowhere to
            // snapshot" is worth telling the user about, "git is broken" is not. `throwOnError: false`
            // is what separates them: git that ran and said no returns an empty string, git that could
            // not run at all throws out of Process.Start into the catch below.
            if ((await git.RunAsync("rev-parse --is-inside-work-tree", false, timed.Token)).Trim() != "true")
                return GoalBaselineResult.NotARepository;

            // A repository nobody has committed in yet is the same answer for the user, for a different
            // reason: there is no HEAD to parent the snapshot on, and nothing to recover *to* either.
            if ((await git.RunAsync("rev-parse --verify HEAD", false, timed.Token)).Trim().Length == 0)
                return GoalBaselineResult.NotARepository;

            var gitDir = (await git.RunAsync("rev-parse --absolute-git-dir", false, timed.Token)).Trim();
            if (gitDir.Length == 0) return GoalBaselineResult.None;

            var tree = await WriteTreeAsync(git, gitDir, timed.Token);

            // An identity of our own, on the command line rather than from the user's configuration.
            // Measured: with `user.name` and `user.email` unset, `commit-tree` fails outright with
            // "Author identity unknown" — so on a machine where git has never been configured the
            // snapshot would silently not exist, which is exactly the machine whose user is least
            // likely to have a second copy of anything.
            //
            // `commit.gpgsign` is turned off explicitly. Measured: `commit-tree` ignores it anyway, so
            // this is belt and braces against a git that changes its mind — and the failure it guards
            // is the worst kind here, a headless process waiting for a passphrase nobody can type.
            var commit = (await git.RunAsync(
                "-c user.name=mTiles -c user.email=mtiles@localhost -c commit.gpgsign=false " +
                $"commit-tree {tree} -p HEAD -m \"mTiles: {what} {goalId}\"",
                timed.Token)).Trim();

            var name = prefix + goalId;
            await git.RunAsync($"update-ref {name} {commit}", timed.Token);

            // After the ref is written, never before: a prune that ran first could leave a workspace
            // with no snapshot at all if what follows it fails.
            await PruneAsync(git, prefix, timed.Token);

            return new GoalBaselineResult(name, NoRepository: false);
        }
        // The caller going away is not a failure to report, and it must not be reported as a workspace
        // without a repository — which would put a note about `git init` in front of somebody who had
        // simply pressed Pause.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning("The goal baseline snapshot ran out of time and was abandoned.");
            return GoalBaselineResult.None;
        }
        catch (Exception ex)
        {
            // Everything reaching here is git working badly rather than a workspace without git in it:
            // the two questions at the top have already settled that, by asking rather than by matching
            // on the wording of an error message — which is git's to change and is translated on a
            // localised install. A broken GitPath, a full disk, an index copied while the tile next
            // door was staging something: a snapshot that did not happen, and nothing the user can act
            // on, so it is logged and not said.
            Trace.TraceWarning($"The goal baseline snapshot failed: {ex.Message}");
            return GoalBaselineResult.None;
        }
    }

    /// <summary>
    /// A tree object holding the working tree exactly as it stands, without disturbing anything.
    /// </summary>
    /// <remarks>
    /// <para>Public because it is the only honest way to diff two moments of a working tree.
    /// <c>git diff &lt;baseline&gt;</c> looks correct and is not: a file that was untracked when the
    /// baseline was taken is in the baseline tree but not in the index, so git reports it
    /// <b>deleted</b> — measured — while it sits untouched on disk. A tool told a file was deleted may
    /// well go and put it back. Tree against tree has no index in it and no such blind spot, which is
    /// why both <see cref="GoalCommitter"/> and <c>WorktreeReader</c> ask for one.</para>
    /// <para>No commit and no ref: the caller uses it immediately and a dangling tree costs nothing
    /// until the next <c>gc</c>, which is exactly the lifetime wanted. <see cref="CaptureAsync"/> is
    /// the one that needs it to survive, and wraps it itself.</para>
    /// </remarks>
    public async Task<string?> TreeNowAsync(CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        try
        {
            var git = new GitCommandRunner(workingDirectory, gitPath);
            var gitDir = (await git.RunAsync("rev-parse --absolute-git-dir", false, timed.Token)).Trim();
            if (gitDir.Length == 0) return null;

            return await WriteTreeAsync(git, gitDir, timed.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Writing a tree for the working tree failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The part of an id that is safe in a ref name and on a command line.</summary>
    /// <remarks>
    /// Git's own rules for a ref are longer than this; what is kept is the intersection of "legal in a
    /// ref" and "means nothing to a shell or to a quoted argument". An id left empty by the filter
    /// still names a snapshot — the ref would collide with the next such id, which is a worse outcome
    /// than a dull name, so a fallback is used rather than an empty one.
    /// </remarks>
    private static string Safe(string goalId)
    {
        var kept = new string([..goalId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')]);
        return kept.Length > 0 ? kept : "goal";
    }

    /// <summary>
    /// Stages the whole working tree into an index of our own and writes the tree it makes.
    /// </summary>
    private async Task<string> WriteTreeAsync(GitCommandRunner git, string gitDir, CancellationToken ct)
    {
        var index = Path.Combine(Path.GetTempPath(), $"mtiles-baseline-{Guid.NewGuid():N}.idx");
        try
        {
            // A **copy** of the real index rather than `read-tree HEAD`, and the difference is not
            // stylistic. `read-tree` produces entries with no stat information, so the `add` below has
            // to re-hash every file in the repository instead of only the ones whose timestamps moved.
            // Measured on this repository: 0.41s against 0.14s, and that gap grows with the size of the
            // repository rather than with the size of the change — on a large one it is the difference
            // between a pause nobody notices and one that needs explaining.
            //
            // A repository nothing has ever been staged in has no index file; there `read-tree` is the
            // correct fallback and its cost is the cost of a repository that small.
            var real = Path.Combine(gitDir, "index");
            if (File.Exists(real))
                File.Copy(real, index, overwrite: true);
            else
                await RunWithIndexAsync("read-tree HEAD", index, ct);

            // Everything, including untracked files — they are the ones `git checkout HEAD -- path`
            // can never bring back. `.gitignore` is honoured, which is what keeps build output and
            // node_modules out of this without a list of guesses maintained here.
            //
            // `.mtiles` is deliberately *not* excluded, unlike in WorktreeReader. That exclusion exists
            // so the agent is not handed this tile's own transcript as "recent changes"; nothing hands
            // a tree object to anybody, and what the *prompt* sees is filtered where the diff is taken.
            await RunWithIndexAsync("add -A", index, ct);
            return (await RunWithIndexAsync("write-tree", index, ct)).Trim();
        }
        finally
        {
            Delete(index);

            // And the lock beside it. `git add` takes `<index>.lock` next to whatever GIT_INDEX_FILE
            // points at, and a git killed by the ten-second budget leaves it there — one file per
            // abandoned snapshot, in the user's temp directory, for ever. Deleting a lock nobody holds
            // is safe here in a way it never is in a repository: this index is ours, it is used by one
            // process, and by this line that process is gone.
            Delete(index + ".lock");
        }
    }

    /// <summary>
    /// Drops all but the newest <see cref="Keep"/> snapshots, newest last by commit date.
    /// <para>Its failure is swallowed on purpose and not reported anywhere the user can see: the
    /// snapshot this run needed has already been written, and "some old refs could not be deleted" is
    /// not a sentence worth putting in a transcript about a goal.</para>
    /// </summary>
    private static async Task PruneAsync(GitCommandRunner git, string prefix, CancellationToken ct)
    {
        try
        {
            var listed = await git.RunAsync(
                $"for-each-ref --sort=committerdate --format=%(refname) {prefix}", ct);
            var refs = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var stale in refs.Take(Math.Max(0, refs.Length - Keep)))
                await git.RunAsync($"update-ref -d {stale}", ct);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Pruning old goal baselines failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One git command against a private index, so nothing here can disturb what the user has staged.
    /// </summary>
    /// <remarks>
    /// <see cref="GitCommandRunner"/> takes no environment, and giving it one would put a knob on the
    /// type every other caller has to read past for a case only this class has. The process is
    /// therefore started here — the same arguments, plus the one variable that makes all of this safe.
    /// </remarks>
    private async Task<string> RunWithIndexAsync(
        string arguments, string indexFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitPath, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_INDEX_FILE"] = indexFile;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git process: git {arguments}");

        // Killed on the way out, and this is not tidiness. Cancellation here is nearly always the ten
        // second budget expiring on a `git add -A` that is hashing something enormous; without this the
        // await returns, the caller's `finally` deletes the index file, and git carries on writing
        // blobs into the user's repository against a path that no longer exists — for as long as it
        // takes, invisibly, after the tile has moved on. `entireProcessTree` because git delegates to
        // helpers, and the whole thing is wrapped: a process that has already exited throws, which
        // would replace the cancellation with an error about nothing.
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {arguments} failed (exit {process.ExitCode}): {(await stderr).Trim()}");

            return await stdout;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);

                // Waited for, or the `finally` above races the dying process for the index file — which
                // is the very thing this was written to stop. Not the cancelled token: the point is to
                // outlive it.
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Killing the abandoned git process failed: {ex.Message}");
            }

            throw;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // A temporary file left behind is litter; a throw in a finally block would replace the
            // real failure with this one.
            Trace.TraceWarning($"Deleting the baseline's temporary index failed: {ex.Message}");
        }
    }
}
