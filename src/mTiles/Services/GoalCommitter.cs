using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>What a run left behind that could be committed, and what was deliberately left out.</summary>
/// <param name="Readable">
/// False where git could not be asked at all — no repository, a broken binary, the ten-second budget
/// spent.
/// <para>Its own field rather than an empty list, because the two call for different sentences and the
/// user can act on one of them. "There is nothing here this run can claim" is a fact about their work;
/// the same sentence over a git that failed is a lie about it, and it sends nobody anywhere.</para>
/// </param>
/// <param name="Files">Paths the run itself changed, relative to the repository root.</param>
/// <param name="LeftAlone">
/// Paths that changed too and are <b>not</b> the run's to commit, because the user had already changed
/// them before the goal started.
/// <para>Named rather than silently dropped. <c>git commit -- path</c> commits the <em>whole file</em>,
/// not the part of it this run wrote, so committing one of these would sweep somebody's unfinished work
/// into a message about something else. Telling them which files those are is the difference between a
/// commit that looks incomplete and one that looks wrong.</para>
/// </param>
/// <param name="TouchedSince">
/// Paths this run did change, and somebody has changed again since it finished — the other Goal tile in
/// the workspace, or the user in the terminal tile next door.
/// <para>Kept apart from <paramref name="LeftAlone"/> because the two are opposite ends of the run and
/// the sentence differs: one is work that was already here, the other is work that arrived afterwards.
/// The consequence is the same and it is the reason both exist — a commit takes the whole file, so
/// either way what would go in is not only this run's.</para>
/// </param>
/// <param name="Bounded">
/// False when nothing recorded how the tree looked as this run finished, so the upper end of the scope
/// is the tree as it is <em>now</em>.
/// <para>That is the old behaviour and it is wrong in exactly one way, which is why it is a field rather
/// than left silent: anything that changed after the run — a second Goal tile finishing, the user's own
/// afternoon — cannot be told from the run's own work, and the person approving the commit is the only
/// one who can say whether that matters. It happens for a goal that ran before this was recorded at all,
/// and for one whose closing snapshot git refused.</para>
/// </param>
internal readonly record struct GoalCommitScope(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> LeftAlone,
    bool Readable = true,
    IReadOnlyList<string>? TouchedSince = null,
    bool Bounded = true)
{
    /// <summary>Git answered, and there is nothing here to commit.</summary>
    public static readonly GoalCommitScope Empty = new([], []);

    /// <summary>Git could not be asked. Not the same thing, and not said the same way.</summary>
    public static readonly GoalCommitScope Unreadable = new([], [], Readable: false);

    /// <summary>The paths somebody touched after this run finished. Never null.</summary>
    public IReadOnlyList<string> TouchedSince { get; init; } = TouchedSince ?? [];

    public bool HasWork => Files.Count > 0;
}

/// <summary>
/// A commit run that stopped part way, and how far it got.
/// </summary>
/// <remarks>
/// The count is the whole reason this type exists: the commits already made are in the user's history
/// and cannot be taken back by anything here, so the message about the failure has to be able to name
/// them and offer the same way out a successful run does.
/// </remarks>
internal sealed class GoalCommitFailure(int made, string message, Exception inner)
    : InvalidOperationException(message, inner)
{
    /// <summary>How many commits of the plan were made before it stopped.</summary>
    public int Made { get; } = made;
}

/// <summary>
/// Commits what a goal run produced, and only that.
/// </summary>
/// <remarks>
/// <para><b>The boundary comes from <see cref="GoalBaseline"/>, which is the whole reason this can be
/// honest.</b> A run is bracketed by two snapshots — the tree as the goal started and the tree as it
/// finished — each a commit object beside the history, parented on the HEAD of its moment. Tree-to-tree
/// comparisons then answer everything: what changed <em>during</em> the run (<c>baseline</c> against
/// <c>end</c>), what the user had already changed before it (<c>baseline^</c> against <c>baseline</c>),
/// what anybody has changed since it finished (<c>end</c> against now), and what is still uncommitted at
/// all (<c>HEAD</c> against now). Tree against tree rather than anything involving the index, because
/// the index is the user's and untracked files are invisible to most of the alternatives.</para>
/// <para><b>Both ends, not just the lower one.</b> With only a baseline, "what this run changed" was
/// read as "everything that has changed since it started" — which is the run's own work in a workspace
/// with one Goal tile in it, and the work of every tile in a workspace with three. Measured the hard
/// way: three tiles finished, Commit was pressed in the first, and all three runs went into the history
/// under the first one's messages. The closing snapshot is what the run's own work is bounded by, and
/// anything after it belongs to whoever made it.</para>
/// <para><b>Without a baseline there is no commit.</b> A run in a workspace that could not be
/// snapshotted has no way to tell its own work from the user's, and "commit everything that is dirty"
/// is exactly the mistake this whole area of the tile exists to stop making.</para>
/// <para><b>The user's staging area survives.</b> Commits are made with <c>git commit --only -- paths</c>,
/// which commits those paths from the working tree and leaves everything else in the index where it
/// was — measured: a file the user had staged is still staged afterwards, and their unrelated
/// uncommitted edit is still uncommitted. An untracked file needs <c>git add -N</c> first, because
/// <c>--only</c> refuses a path git has never heard of.</para>
/// <para><b>Hooks and signing are the user's.</b> Nothing here passes <c>--no-verify</c> or
/// <c>--no-gpg-sign</c>: a repository whose pre-commit hook rejects this work is a repository saying no,
/// and the right response is to report it rather than to go around it. That is also why the whole thing
/// is bounded by a timeout — a signing key with a passphrase and no agent would otherwise leave a
/// process waiting for a prompt nobody can see.</para>
/// </remarks>
internal sealed class GoalCommitter(string workingDirectory, string gitPath)
{
    /// <summary>
    /// How long the commits may take. Generous next to <see cref="GoalBaseline"/>'s, because this one
    /// runs the user's own pre-commit hooks and those legitimately build and test.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    /// <summary>The repository's own top directory, remembered for the life of this committer.</summary>
    private string? _root;

    /// <summary>
    /// Where every git command here has to run from.
    /// </summary>
    /// <remarks>
    /// <para>A workspace is often a subdirectory of the repository — one service of a monorepo, the
    /// <c>src</c> of something bigger — and git treats its two ends differently: the paths it
    /// <em>prints</em> (<c>diff --name-only</c>) are relative to the top of the repository, while the
    /// paths it is <em>given</em> (a pathspec after <c>--</c>) are relative to the current directory.
    /// Measured in this repository, not assumed: from <c>sub/</c>, a diff names
    /// <c>src/mTiles/Views/GoalTileView.axaml</c> and a pathspec spelled that way matches nothing.</para>
    /// <para>So the scope this class computes and the commits it makes were in two different coordinate
    /// systems, and committing from a subdirectory failed with "pathspec did not match any file(s)" —
    /// or, where the same relative path happened to exist under the workspace, committed the wrong
    /// file. Running everything from the top puts both ends in the coordinate system the printed paths
    /// are already in, so nothing has to be translated.</para>
    /// <para>Falls back to the workspace when git cannot answer. That is the case where there is no
    /// repository at all, and every command after it will fail for that reason and be reported.</para>
    /// </remarks>
    private async Task<string> RootAsync(CancellationToken ct)
    {
        if (_root is { } known) return known;

        var top = (await new GitCommandRunner(workingDirectory, gitPath)
            .RunAsync("rev-parse --show-toplevel", false, ct)).Trim();

        return _root = top.Length > 0 ? top : workingDirectory;
    }

    /// <summary>
    /// Works out which files this run may commit, from the baseline it started with.
    /// </summary>
    /// <param name="existingWork">
    /// True when this goal is about the work that was already in the tree — the detect paths, where
    /// the user pointed at their uncommitted changes and said "this is the goal".
    /// <para>It collapses the distinction the rest of this class is built on. "Theirs" normally means
    /// the work this run had no right to touch; there, it is the subject of the run. Left in, every
    /// file fell into <c>LeftAlone</c>, the scope came back empty, and the summary offered a Commit
    /// button whose only possible outcome was "there is nothing here this run can claim".</para>
    /// <para>It is the same statement <c>ReviewsExistingWork</c> makes about the diff: measure from
    /// HEAD, because the changes are the point. Making one of the two say it and not the other is how
    /// the tile ended up reviewing one set of files and offering to commit another.</para>
    /// </param>
    /// <param name="endRef">
    /// The ref holding the tree as this run finished, or null when none was recorded.
    /// <para>Null is the honest unknown rather than a default: a goal that ran before this was recorded,
    /// or one whose closing snapshot git refused. The scope is then bounded at <em>now</em> exactly as
    /// it used to be, and says so — see <see cref="GoalCommitScope.Bounded"/> — because the alternative
    /// is refusing to commit reviewed work over a snapshot the user never asked for.</para>
    /// </param>
    public async Task<GoalCommitScope> ScopeAsync(string baselineRef, string? endRef,
        CancellationToken ct, bool existingWork = false)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        try
        {
            var git = new GitCommandRunner(await RootAsync(timed.Token), gitPath);

            // A tree, not a snapshot. This used to call CaptureAsync, which writes a commit, writes a
            // ref under refs/mtiles/goals/, and prunes that namespace to the newest twenty — and the
            // scratch ref it had just written was the newest, so it survived the prune and evicted the
            // oldest *real* baseline. Committing a run quietly destroyed somebody's oldest way back.
            //
            // A tree object is still what both comparisons need: it is the only thing that holds
            // untracked files, so it is the only thing two of these can be made against without asking
            // the index anything.
            // A tree that could not be written is git failing, not a clean working directory.
            var now = await new GoalBaseline(workingDirectory, gitPath).TreeNowAsync(timed.Token);
            if (now is null) return GoalCommitScope.Unreadable;

            // `.mtiles/` is excluded from every comparison here, and that is not tidiness. Nothing adds
            // it to a workspace's .gitignore unless a Git tile has been opened there, so in every other
            // workspace this tile's own state file — rewritten after every message of the run that is
            // about to be committed — is an ordinary untracked file. It would arrive in the scope, be
            // handed to the tool as one of "the files this run changed", and land in somebody's history
            // under a message about their feature. The reader keeps it out of what the agent is shown
            // for the same reason; this is the other end of the same rule.

            // What still differs from HEAD, which is the only thing `commit --only` can act on. Without
            // it, a run continued after committing offered its first batch a second time and the commit
            // failed outright: those files now match HEAD exactly, so there is nothing there to commit.
            // It also quietly covers the user committing some of this by hand in the terminal tile next
            // door.
            var uncommitted = await NamesAsync(
                git,
                $"diff --name-only --no-renames HEAD^{{tree}} {now} -- {WorktreeReader.Excluded}",
                timed.Token);

            // The run's own upper end. Without a closing snapshot there is none, so the tree as it is
            // now stands in for it — which is what lets a later run's work look like this one's, and is
            // reported rather than assumed away.
            //
            // **Verified, not assumed.** These refs are pruned to the newest twenty of their namespace,
            // and a tile keeps its `EndRef` in its state file for as long as the tile exists — so a
            // goal reopened after twenty later summaries in the same workspace holds a ref git no longer
            // has. Used unchecked, the very first `diff` against it fails, the catch below reports
            // `Unreadable`, and the dialog tells the user git could not be asked at all: a run that
            // committed perfectly well before the closing snapshot existed became one that cannot be
            // committed because of it. A missing end is exactly the case `Bounded: false` already
            // describes and the dialog already explains.
            var bounded = endRef is { Length: > 0 } && await ExistsAsync(git, endRef, timed.Token);
            var end = bounded ? $"{endRef}^{{tree}}" : now;

            // On the detect paths that is also the answer to "what did this run change", because there
            // the run is about everything that was already here — bounded at the same closing snapshot,
            // for the same reason: the tree it was about is the one this run finished with, not the one
            // the tile next door has been writing to since.
            var changed = existingWork
                ? await NamesAsync(git,
                    $"diff --name-only --no-renames HEAD^{{tree}} {end} -- {WorktreeReader.Excluded}",
                    timed.Token)
                : await NamesAsync(git,
                    $"diff --name-only --no-renames {baselineRef}^{{tree}} {end} -- {WorktreeReader.Excluded}",
                    timed.Token);

            // Whatever has moved since this run finished, whoever moved it. A second Goal tile, or the
            // user in the terminal tile next door — not told apart, because a commit takes the whole
            // file and the consequence of getting it wrong is identical either way.
            //
            // Empty when there is no closing snapshot: `end` is then `now` and the diff is of a tree
            // against itself. That is the unbounded case, and `Bounded` is what says so.
            var touchedSince = await NamesAsync(
                git,
                $"diff --name-only --no-renames {end} {now} -- {WorktreeReader.Excluded}",
                timed.Token);

            // What the user had already changed when the goal started — the baseline's tree against
            // the tree of the commit it was parented on. Excluded whatever the tool proposes, because a
            // commit takes the whole file and half of one of these is somebody's unfinished afternoon.
            //
            // Except where that work *is* the goal: see existingWork.
            var theirs = existingWork
                ? []
                : await NamesAsync(
                    git,
                    $"diff --name-only --no-renames {baselineRef}^^{{tree}} {baselineRef}^{{tree}} " +
                    $"-- {WorktreeReader.Excluded}",
                    timed.Token);

            var mine = changed.Where(uncommitted.Contains).ToList();

            // Held back for either reason, and named under whichever one applies. A file in both lists
            // is reported as somebody's pre-existing work: that is the older claim on it, and naming one
            // file twice in one dialog reads as a bug in the dialog.
            var held = mine.Where(f => theirs.Contains(f) || touchedSince.Contains(f)).ToList();

            return new GoalCommitScope(
                [..mine.Where(f => !held.Contains(f))],
                [..held.Where(theirs.Contains)],
                Readable: true,
                TouchedSince: [..held.Where(f => !theirs.Contains(f))],
                Bounded: bounded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Working out what a goal run changed failed: {ex.Message}");
            return GoalCommitScope.Unreadable;
        }
    }

    /// <summary>Whether a ref is still there to be read.</summary>
    /// <remarks>
    /// <c>--verify --quiet</c> so a missing ref is an exit code rather than a message on stderr, and
    /// the non-zero that follows is caught here rather than left to the caller — the whole point is to
    /// find out without the finding out being the failure. The <c>^{tree}</c> is asked for as well as
    /// the ref, because that is the form every comparison below uses and a ref whose commit is there
    /// but whose tree is not is unusable in the same way.
    /// </remarks>
    private static async Task<bool> ExistsAsync(GitCommandRunner git, string reference, CancellationToken ct)
    {
        try
        {
            await git.RunAsync($"rev-parse --verify --quiet {reference}^{{tree}}", ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Gone, or never written. Either way there is no upper end to bound the run at, which is a
            // scope this already knows how to describe.
            return false;
        }
    }

    /// <summary>
    /// Makes the commits, in the order they were planned, and answers with how many were made.
    /// </summary>
    /// <remarks>
    /// Stops at the first failure rather than carrying on. A hook that rejects the second commit will
    /// reject the third, and pressing on would leave a repository half committed by something the user
    /// would then have to unpick; stopping leaves a prefix of the plan, every commit of it whole.
    /// </remarks>
    public async Task<int> CommitAsync(IReadOnlyList<GoalCommit> commits, CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        var root = await RootAsync(timed.Token);
        var git = new GitCommandRunner(root, gitPath);
        var made = 0;

        foreach (var commit in commits)
        {
            // What this run put into the index for this commit, so a failure can take out exactly
            // that and nothing the user staged themselves.
            IReadOnlyCollection<string> untracked = [];

            // The paths for this commit, in a file rather than on the command line.
            using var pathspec = new PathspecFile(commit.Files);

            try
            {
                // Intent-to-add, so `--only` will accept a path git has never seen. Measured: without
                // it the commit fails outright with "pathspec did not match any file(s) known to git",
                // and a new file is most of what an implementation produces.
                //
                // Only the paths still on disk, and that filter is not an optimisation. A path the run
                // deleted — or renamed away from, which is a deletion under another name — is not in
                // the working tree and may not be in the index either, and `git add -N` answers a
                // pathspec matching nothing with `fatal:` and adds *none* of the others. So one
                // renamed file took the whole commit down with it, and a genuinely new file beside it
                // was never registered. Deleted paths need no intent-to-add anyway: git already knows
                // them, which is how it knows they are gone.
                // Against the root, for the same reason the commands run there: these paths came out
                // of a diff and are relative to it, so joining them to the workspace asked about a
                // file one directory too deep — and answered no, which quietly dropped the
                // intent-to-add that a new file needs.
                var present = commit.Files
                    .Where(f => File.Exists(Path.Combine(root, f)))
                    .ToList();

                // Which of them git has never heard of — the ones `add -N` will actually register, and
                // therefore the only ones this run may take back out of the index if the commit fails.
                // Asked rather than assumed: `add -N` on a tracked path is a no-op, so a reset covering
                // it would be undoing staging that was already there and is not ours.
                //
                // In batches, because `ls-files` is the one command here with no --pathspec-from-file.
                // Splitting a question is safe in a way splitting a commit is not: the answers add up.
                untracked = await UntrackedAsync(git, present, timed.Token);

                using var toAdd = new PathspecFile(present);
                if (present.Count > 0)
                    await git.RunAsync($"add -N {toAdd.Argument}", timed.Token);

                // --only: these paths from the working tree, and nothing else that happens to be
                // staged. Without it a commit here would carry whatever the user had prepared in
                // another tile, under a message about the goal.
                // -F rather than -m, and here the difference is correctness rather than style. The
                // message is written by a model, from a prompt carrying a working tree that can contain
                // anything, and GitCommandRunner builds one command-line string: a subject ending in a
                // backslash, holding an unbalanced quote, or spanning two lines is either mangled or
                // rewrites the rest of the command. A file has no such grammar.
                var message = Path.Combine(Path.GetTempPath(), $"mtiles-commit-{Guid.NewGuid():N}.txt");
                try
                {
                    // UTF-8 without a BOM: git reads this as bytes, and a BOM would become the first
                    // three characters of every subject.
                    await File.WriteAllTextAsync(message, commit.Message,
                        new System.Text.UTF8Encoding(false), timed.Token);
                    await git.RunAsync(
                        $"commit --only -F {Quote(message)} {pathspec.Argument}", timed.Token);
                }
                finally
                {
                    try { File.Delete(message); }
                    catch (Exception ex) { Trace.TraceWarning($"Deleting {message} failed: {ex.Message}"); }
                }

                made++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The intent-to-add entries are taken back out, or a refused commit leaves the user
                // looking at files git now claims are staged and empty.
                Trace.TraceWarning($"Committing '{commit.Message}' failed: {ex.Message}");

                // Only the intent-to-add entries this run made. `reset -- <every path in the commit>`
                // was taking the user's own staging with it: they work in the terminal tile next door
                // while a goal runs, and a `git add -p` finished five minutes ago is exactly the kind
                // of thing a failed commit must not quietly discard.
                if (untracked.Count > 0)
                {
                    using var toReset = new PathspecFile(untracked);
                    await TryAsync(git, $"reset -q {toReset.Argument}", CancellationToken.None);
                }

                // The count travels with the failure, so the caller can say *which* commits are now in
                // the history. A partial failure is the one outcome where the user has to know that
                // exactly — some of their work has moved and the rest has not — and a sentence naming
                // a number leaves them to work the rest out from git log.
                throw new GoalCommitFailure(made,
                    $"Stopped after {made} commit{(made == 1 ? "" : "s")}: {ex.Message}", ex);
            }
        }

        return made;
    }

    /// <summary>
    /// The paths one diff names, read so that every path survives being read.
    /// </summary>
    /// <remarks>
    /// <para><c>-z</c> is not an optimisation. With <c>core.quotepath</c> at its default, git renders
    /// anything outside ASCII as a quoted escape — measured, <c>src/żółw.txt</c> comes back as
    /// <c>"src/\305\274\303\263\305\202w.txt"</c> — which matches nothing the tool named, so the file is
    /// silently never committed. <c>-z</c> emits raw bytes separated by NUL, which also removes the only
    /// other way a path can be misread here: a newline inside a filename.</para>
    /// <para>No <c>TrimEntries</c>: a leading or trailing space is a legal part of a path, and trimming
    /// one turns a real file into a name that does not exist.</para>
    /// <para><b>And it goes in front of any <c>--</c>.</b> Everything after that separator is a
    /// pathspec, so a <c>-z</c> tacked on the end stopped being an option and became a file called
    /// <c>-z</c> — which matches nothing, so every diff carrying an exclusion came back empty and the
    /// run had nothing to commit. It cost a green test suite to find and one line to say.</para>
    /// </remarks>
    private static async Task<HashSet<string>> NamesAsync(
        GitCommandRunner git, string arguments, CancellationToken ct)
    {
        var at = arguments.IndexOf(" -- ", StringComparison.Ordinal);
        var output = await git.RunAsync(
            at < 0 ? arguments + " -z" : arguments.Insert(at, " -z"), ct);
        return [..output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>A command whose failure changes nothing worth reporting — tidying up after something
    /// that has already succeeded, or after something that has already failed.</summary>
    private static async Task TryAsync(GitCommandRunner git, string arguments, CancellationToken ct)
    {
        try
        {
            await git.RunAsync(arguments, false, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"git {arguments} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// How many paths may go on one command line at a time.
    /// </summary>
    /// <remarks>
    /// Only <c>ls-files</c> needs this — every other command here takes its pathspec from a file. A
    /// hundred paths at, say, 120 characters each is comfortably inside the 32 767 Windows allows even
    /// once <c>:(literal)</c> and the quotes are counted.
    /// </remarks>
    private const int PathsPerQuery = 100;

    /// <summary>Which of these paths git has never heard of.</summary>
    /// <remarks>
    /// A query, so it may be split: the union of the answers is the answer. A commit may not, which is
    /// why that one is given its paths in a file instead.
    /// </remarks>
    private static async Task<HashSet<string>> UntrackedAsync(
        GitCommandRunner git, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        for (var at = 0; at < paths.Count; at += PathsPerQuery)
        {
            var batch = paths.Skip(at).Take(PathsPerQuery);
            found.UnionWith(await NamesAsync(
                git, $"ls-files --others --exclude-standard -- {Paths(batch)}", ct));
        }

        return found;
    }

    /// <summary>Several paths as one pathspec list.</summary>
    private static string Paths(IEnumerable<string> paths) => string.Join(" ", paths.Select(Literal));

    /// <summary>
    /// A commit's paths, written to a temporary file for <c>--pathspec-from-file</c>.
    /// </summary>
    /// <remarks>
    /// <para>Windows caps a command line at 32 767 characters, and a commit plan can name hundreds of
    /// files — the single sweeping commit this tile falls back to names <em>all</em> of them. Past that
    /// cap <c>Process.Start</c> throws, and the failure lands in the middle of a plan whose earlier
    /// commits are already in the user's history.</para>
    /// <para>Batching is not available here: a commit is one act, and splitting its pathspec makes two
    /// commits with two messages, which is a different answer to the question the user approved.
    /// <c>--pathspec-from-file</c> is git's own way out, and <c>--pathspec-file-nul</c> is what makes it
    /// safe for names containing quotes, backslashes or newlines. Each entry still carries
    /// <c>:(literal)</c>: NUL separation settles the quoting, not the glob magic.</para>
    /// <para>Deleted on disposal, and a failure to delete is a warning rather than an exception: the
    /// commit has either happened or not by then, and neither outcome is improved by throwing over a
    /// temporary file.</para>
    /// </remarks>
    private sealed class PathspecFile : IDisposable
    {
        private readonly string _path;

        public PathspecFile(IEnumerable<string> paths)
        {
            _path = Path.Combine(Path.GetTempPath(), $"mtiles-paths-{Guid.NewGuid():N}.txt");

            // No BOM and no trailing separator: git reads this as bytes and an empty final element is
            // a pathspec matching nothing, which would fail the whole command.
            File.WriteAllText(
                _path,
                string.Join('\0', paths.Select(p => ":(literal)" + p)),
                new System.Text.UTF8Encoding(false));
        }

        /// <summary>The two flags and the file, ready to go on a command line.</summary>
        public string Argument => $"--pathspec-from-file={Quote(_path)} --pathspec-file-nul";

        public void Dispose()
        {
            try { File.Delete(_path); }
            catch (Exception ex) { Trace.TraceWarning($"Deleting {_path} failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// A path as a pathspec git will match literally.
    /// </summary>
    /// <remarks>
    /// <para>A bare pathspec is a <em>pattern</em>: <c>*</c>, <c>?</c> and <c>[...]</c> are glob magic,
    /// and a leading colon introduces magic of its own. A file actually named <c>Report[1].cs</c> —
    /// ordinary enough on a machine where something downloaded it twice — therefore does not match
    /// itself, <c>commit --only</c> answers "did not match any file(s)", and it takes the rest of the
    /// plan down with it in the middle of a run that has already made two commits.</para>
    /// <para><c>:(literal)</c> is git's own way of saying that these characters are the name. It has
    /// been there since 1.9, which is older than anything that can run the rest of this.</para>
    /// </remarks>
    private static string Literal(string path) => Quote(":(literal)" + path);

    /// <summary>
    /// One argument, quoted for the command line <see cref="GitCommandRunner"/> builds.
    /// </summary>
    /// <remarks>
    /// Needed rather than tidy: these are paths and commit subjects, and both routinely contain spaces.
    /// A subject is also the one string here written by a model from a prompt carrying a working tree,
    /// so the embedded quotes are escaped rather than assumed away.
    /// </remarks>
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
