using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace mTiles.Services;

/// <summary>
/// What the working tree looked like, and whether anybody actually managed to look.
/// </summary>
/// <param name="Text">The block for the prompt, or null for a clean tree. <b>Clipped</b>, and therefore
/// never the thing to compare two reads with — see <paramref name="Fingerprint"/>.</param>
/// <param name="Readable">
/// False when git could not be run at all — no repository, a bad <c>GitPath</c>, a broken install.
/// <para>Its own field because the alternative is inferring it from <see cref="Text"/>, and that
/// inference was wrong in a way nothing noticed: a workspace that is not a repository produces the
/// <em>same</em> text on every read — either null, or a stable "the tree could not be read" note — so
/// comparing two of them said the implementation had changed nothing, and every goal ended after one
/// attempt with a confident and false explanation. "I could not tell" is not "nothing happened".</para>
/// </param>
/// <param name="Fingerprint">
/// A digest of the <em>whole</em> git output, before any of it was cut to fit a prompt. Null when
/// nobody could read the tree.
/// <para>Its own field because <see cref="Text"/> cannot answer the question it was being asked.
/// <c>GoalDiffContext.Compose</c> clips the diff to 6 000 characters and the untracked list to 1 000, so
/// two reads of a large working tree are character-for-character identical whenever the change falls
/// past the cut — which is the ordinary case on the resume-after-a-big-implementation the no-change
/// stop exists for. The run then ended after one attempt saying "the last implementation changed nothing
/// in the working tree": confident, specific and false. The same class of bug
/// <see cref="Readable"/> was added to close, one layer along.</para>
/// <para>A digest rather than the text: two snapshots are held at once and a diff is measured in
/// megabytes.</para>
/// </param>
internal readonly record struct WorktreeSnapshot(string? Text, bool Readable, string? Fingerprint = null)
{
    public static readonly WorktreeSnapshot Unreadable = new(null, false);

    /// <summary>
    /// Whether two snapshots <b>prove</b> the tree stood still.
    /// <para>Two unreadable ones prove nothing, and neither does a missing fingerprint: the question is
    /// only ever asked to stop a run, so anything short of proof has to answer no.</para>
    /// </summary>
    public bool ProvablyUnchangedFrom(WorktreeSnapshot other) =>
        Readable && other.Readable
        && Fingerprint is { } mine && other.Fingerprint is { } theirs
        && mine == theirs;

    /// <summary>What a read of this exact output should be remembered by.</summary>
    public static string Digest(string whole) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(whole)));
}

/// <summary>
/// What the working tree currently looks like, as one block of text for a prompt.
/// <para>Its own class rather than three methods on the Goal tile's view model: reading a repository is
/// not the view model's job, and while it lived there every test that drove the loop spawned four git
/// processes a lap against whatever directory it happened to be given. <see cref="Factory"/> is the
/// seam that stops it — the same one the AI runner and the launch chain's PTY use.</para>
/// <para>The assembly of the pieces is in <see cref="GoalDiffContext"/> and stays pure; this class is
/// only the part that talks to git.</para>
/// <para>Every command carries <c>--no-optional-locks</c>. These run unprompted — the availability check
/// on every idle moment, the tree read twice a lap — in a repository the user is working in, and git
/// refreshes its index while reading unless told not to. Taking that lock behind somebody's back is how
/// a rebase in the terminal tile next door fails with "index.lock: File exists" because a tile was
/// deciding whether to show a button.</para>
/// </summary>
internal sealed class WorktreeReader(string workingDirectory, string gitPath)
{
    /// <summary>Replaced by a test. Null means read the real repository.</summary>
    internal static Func<string, CancellationToken, Task<string?>>? Factory { get; set; }

    /// <summary>
    /// Whether there is anything uncommitted here at all — the question the "Detect goal" button asks
    /// before it offers itself.
    /// <para>One <c>git status --porcelain</c> rather than the two commands <see cref="ReadAsync"/>
    /// runs, because the answer is a yes or a no and the tile asks it whenever it goes idle.</para>
    /// <para>A repository that cannot be read answers <b>no</b>. The button offers to work out a goal
    /// from the changes; offering that where the changes cannot be read is offering a run that can only
    /// fail.</para>
    /// </summary>
    public async Task<bool> HasChangesAsync(CancellationToken ct)
    {
        if (Factory is { } stub)
            return !string.IsNullOrWhiteSpace(await stub(workingDirectory, ct));

        try
        {
            var git = new GitCommandRunner(workingDirectory, gitPath);
            var status = await git.RunAsync(
                "--no-optional-locks status --porcelain -- \":(exclude).mterminal\"", ct);
            return status.Trim().Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Reading git status failed: {ex.Message}");
            return false;
        }
    }

    public async Task<WorktreeSnapshot> ReadAsync(CancellationToken ct)
    {
        // A stub is a fixture, not git: whatever it says, it said it successfully. Its text is not
        // clipped by anything, so it is its own fingerprint.
        if (Factory is { } stub)
        {
            var stubbed = await stub(workingDirectory, ct);
            return new WorktreeSnapshot(stubbed, Readable: true, WorktreeSnapshot.Digest(stubbed ?? ""));
        }

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
                git, "--no-optional-locks diff HEAD -- \":(exclude).mterminal\"", ct);
            // `.mterminal` excluded by pathspec. Without a Git tile in the workspace nothing adds it to
            // .gitignore, so the listing included this tile's own state file — the transcript being
            // rewritten after every message — and handed the agent its path to tidy up or edit.
            var (untracked, untrackedProblem) = await RunAsync(
                git, "--no-optional-locks ls-files --others --exclude-standard -- \":(exclude).mterminal\"", ct);

            // Both problems, joined: reporting only the first hid the second, and the two commands fail
            // for different reasons.
            var problems = string.Join("; ", new[] { diffProblem, untrackedProblem }.Where(x => x != null));

            // Readable when *either* command worked. `diff HEAD` legitimately fails in a repository
            // with no commits yet while the listing succeeds, and that tree is perfectly readable —
            // demanding both would have called it unreadable and disabled the checks that depend on it.
            var readable = diffProblem == null || untrackedProblem == null;

            // The fingerprint is taken from the raw output of both commands, not from the composed
            // block: what goes in the prompt is cut to fit, and comparing two cut versions is how a
            // change past the cut became "nothing happened". The problems are in it too, so a tree that
            // starts or stops being readable counts as a tree that moved.
            var whole = string.Join("\u0000", diff, untracked, problems);

            return new WorktreeSnapshot(
                GoalDiffContext.Compose(diff, untracked, problems),
                readable,
                readable ? WorktreeSnapshot.Digest(whole) : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Reading the working tree failed: {ex.Message}");
            return WorktreeSnapshot.Unreadable;
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
