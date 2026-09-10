using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// What a run is asked to look at, when the two ends are not "HEAD" and "the working tree".
/// </summary>
/// <param name="Base">
/// The older end, as a commit id this repository has already resolved.
/// <para><b>The id and not the token, because the token moves.</b> <c>HEAD~1</c> is a different
/// commit the moment anybody commits — in the terminal tile next door, or between closing the tile
/// and pressing Resume tomorrow — so a run that pinned nothing would quietly stop covering the commit
/// its goal was about, and the reviewer would be handed a block with the work under judgement missing
/// from it. Resolving once, when the scope is worked out, is what makes "a Resume tomorrow reads the
/// same two ends" true rather than a hope.</para>
/// </param>
/// <param name="Head">
/// The newer end, or null for the working tree as it stands right now. A commit id when it is not.
/// <para>Null is the ordinary answer and the more useful one: <c>@HEAD~1</c> means "the last commit
/// <em>and</em> whatever I have not committed since", which is what somebody asking about their last
/// commit while still working on it wants. A named head is the other question — <c>@master..HEAD</c>
/// compares two commits and leaves the working tree out of it.</para>
/// </param>
/// <param name="Named">
/// How the user spelled the older end, kept for the one thing an id is worse at: saying so.
/// <para>The plan's block is headed <em>Already changed in this project since …</em>, and
/// <c>HEAD~1</c> there tells a reader what they typed while a forty-character id tells them nothing.
/// It is used for naming and never for reading, so the two cannot disagree about which commit is
/// meant. Null where nothing was typed — a scope restored from a file written before this existed,
/// which then names itself by its id and is still right.</para>
/// </param>
public readonly record struct GoalReadBase(string Base, string? Head, string? Named = null)
{
    /// <summary>What to call the older end in a sentence a person reads.</summary>
    public string Spelling => Named is { Length: > 0 } typed ? typed : Base;
}

/// <summary>
/// The <c>@</c> tokens that name a commit rather than a file.
/// </summary>
/// <remarks>
/// <para><b>One marker, two meanings, and neither is guessed at.</b> <c>@</c> in the composer has always
/// named a path; a path is what the filesystem says exists, and everything left over is offered to
/// <c>git rev-parse</c>. Whichever answers, answers — so <c>@src/Auth.cs</c> is a file, <c>@HEAD~1</c> is
/// a commit, and <c>@admin</c> is neither and changes nothing. A token that is both a live path and a
/// resolvable ref is taken as the path: the file is the thing the user can see.</para>
/// <para><b>What is asked of git is a token shaped like a ref, and never a bare word</b> — see
/// <see cref="NamesARef"/>. This is not the spelling rule that used to stand in front of the path
/// half and is gone for good reason; it is the one question the filesystem has already answered no
/// to, and the branch namespace is full of ordinary words.</para>
/// <para>Its own class because the question is git's and the answer is one small record, while
/// <see cref="GoalScopeFilter"/> is pure text and must stay so.</para>
/// </remarks>
internal static class GoalScopeRef
{
    /// <summary>
    /// The same two ends, with the newer one moved back to the working tree as it stands.
    /// </summary>
    /// <remarks>
    /// <para><b>A pinned newer end is a question about the past, and a run that writes is not asking
    /// it.</b> <c>@master..HEAD</c> names two commits that nothing the tool does can move: the
    /// implementation writes files, the next read comes back byte for byte identical, the review
    /// repeats the findings it has already made, and the run ends on no progress or on spent attempts
    /// with the fixes sitting on disk unlooked at.</para>
    /// <para>The base end is kept, because that is the part the user pointed at — which stretch of the
    /// history this is about. What replaces the head end is the only end that can show the work, so
    /// <c>@master..HEAD</c> is read as <c>master</c> against the tree: those commits <em>and</em>
    /// everything written since, which is a superset of what was named and never less.</para>
    /// <para>Detection is the one read that keeps both ends, and it keeps them because it judges a
    /// range and changes nothing.</para>
    /// </remarks>
    public static GoalReadBase? EndingAtTheWorkingTree(GoalReadBase? scope) =>
        scope is { } named ? named with { Head = null } : null;

    /// <summary>How long the whole resolution may take. It is two or three <c>rev-parse</c> calls in
    /// the ordinary case and runs while the user is waiting for a button to do something.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>Replaced by a test. Null means ask the real repository.</summary>
    internal static Func<string, CancellationToken, Task<GoalReadBase?>>? Factory { get; set; }

    /// <summary>
    /// The first token that names something this repository can resolve, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para><b>The first, not all of them.</b> Two ends is what a diff has; a third would have to be
    /// dropped, and dropping one silently is how a run comes to answer about a range nobody asked
    /// for. A second ref is left where it is — in the words the prompt already carries — so the tool
    /// can see it was named.</para>
    /// <para>Every failure answers null, including a repository that is not one and a git that will
    /// not run: the caller then reads the tree exactly as it does today, which is the answer a user
    /// who typed no ref at all would have got.</para>
    /// </remarks>
    /// <param name="candidates">The <c>@</c> tokens that did not name a live path.</param>
    public static async Task<GoalReadBase?> ResolveAsync(
        IReadOnlyList<string> candidates, string workingDirectory, string gitPath,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return null;

        if (Factory is { } stub) return await stub(candidates[0], ct);

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        try
        {
            var git = new GitCommandRunner(workingDirectory, gitPath);

            foreach (var token in candidates)
            {
                // Explicit rather than a positional pattern on the nullable: whether "is var (a, b)"
                // null-checks is exactly the sort of thing a reader should not have to look up.
                if (!NamesARef(token)) continue;
                if (Split(token) is not { } ends) continue;
                var (left, right) = ends;

                if (await CommitOfAsync(git, left, timed.Token) is not { } baseCommit) continue;

                string? headCommit = null;
                if (right is not null
                    && (headCommit = await CommitOfAsync(git, right, timed.Token)) is null) continue;

                return new GoalReadBase(baseCommit, headCommit, left);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A ref that could not be checked is a ref nobody named: the read falls back to the tree
            // against HEAD, which is where it would have been anyway.
            Trace.TraceWarning($"Resolving a scope ref failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Whether this token is spelled like a commit at all, asked before git is.
    /// </summary>
    /// <remarks>
    /// <para><b>A bare word is prose, whatever the branch list happens to say.</b> The filesystem has
    /// already answered no by the time a token arrives here, and what is left of "popraw formularz
    /// <c>@admin</c>" is a word somebody wrote in a sentence. Offered to <c>rev-parse</c> in a
    /// repository that has a branch called <c>admin</c> — or <c>dev</c>, <c>stable</c>,
    /// <c>release</c> — it resolved, and the whole history since that branch was then read as "the
    /// changes that were just made": a review over hundreds of unrelated files, a typed run entering
    /// at the review because something survived the narrowing, and nothing anywhere on screen saying
    /// which two ends were being compared. The branch namespace is full of ordinary words, so
    /// answering this one with git alone asks a question whose false yes is a whole wasted run and
    /// whose false no costs two keystrokes.</para>
    /// <para><b>It is not the spelling rule that used to stand in front of the path half.</b> That
    /// one dropped <c>@frontend</c> before the filesystem was ever asked, so pointing at a directory
    /// narrowed nothing; it is gone, and this changes nothing about it. A path is still whatever
    /// exists on disk, and a token that exists never reaches this method.</para>
    /// <para>What counts as ref-shaped is punctuation no word carries — <c>~</c>, <c>^</c>, a range,
    /// a remote's slash, <c>@{…}</c>, the dot of a version tag — a commit id, or <c>HEAD</c> itself
    /// and the other <c>_HEAD</c> pseudo-refs git writes. A branch whose name is a plain word is
    /// named the way git names a range: <c>@master..</c>, which is <c>master..HEAD</c> by git's own
    /// rule for an omitted side. That is the commits alone where the bare token also carried whatever
    /// was uncommitted — and only for the two reads that judge a range, since every read of a run
    /// that writes ends at the working tree regardless (<see cref="EndingAtTheWorkingTree"/>).</para>
    /// </remarks>
    private static bool NamesARef(string token)
    {
        if (token.Length == 0) return false;

        if (token.Equals("HEAD", StringComparison.Ordinal)
            || token.EndsWith("_HEAD", StringComparison.Ordinal)) return true;

        if (token.IndexOfAny(RefPunctuation) >= 0) return true;

        return IsCommitId(token);
    }

    /// <summary>Characters a ref carries and a word does not.</summary>
    private static readonly char[] RefPunctuation = ['~', '^', ':', '/', '.', '{', '}', '@'];

    /// <summary>
    /// Whether the token is an abbreviated commit id.
    /// </summary>
    /// <remarks>Seven is git's own shortest abbreviation, which is also what keeps <c>added</c>,
    /// <c>ffff</c> and every other short hex-looking word out; forty is the longest a sha can be.
    /// </remarks>
    private static bool IsCommitId(string token) =>
        token.Length is >= 7 and <= 40 && token.All(Uri.IsHexDigit);

    /// <summary>
    /// A token as its two ends: <c>A..B</c> is a range, anything else is one ref against the working
    /// tree.
    /// </summary>
    /// <remarks>An omitted side means <c>HEAD</c>, which is git's own rule for <c>..</c> and the one
    /// thing a user typing <c>@master..</c> can reasonably expect. Three dots are not supported and
    /// fall through as a single token, which then fails to resolve: a symmetric difference is a
    /// different question from the one this tile asks, and answering it as though it were the ordinary
    /// range would be worse than not answering.</remarks>
    private static (string Left, string? Right)? Split(string token)
    {
        if (token.Contains("...", StringComparison.Ordinal)) return null;

        var at = token.IndexOf("..", StringComparison.Ordinal);
        if (at < 0) return (token, null);

        var left = token[..at].Trim();
        var right = token[(at + 2)..].Trim();

        return (left.Length == 0 ? "HEAD" : left, right.Length == 0 ? "HEAD" : right);
    }

    /// <summary>
    /// The commit git resolves this to, or null when it resolves to none.
    /// </summary>
    /// <remarks>
    /// <para><c>^{commit}</c> is what makes the question the right one: <c>rev-parse --verify</c>
    /// alone accepts a blob or a tree id, which would then fail two commands later inside a diff of
    /// trees. <c>--quiet</c> keeps a refusal off stderr, since a token that is not a ref is the
    /// ordinary case here rather than a fault.</para>
    /// <para><b>The answer is kept rather than thrown away</b>, which is the whole of the pinning:
    /// the same command that says whether a token names a commit says <em>which</em>, and the id it
    /// prints goes on being that commit after the branch has moved on. Answering only yes or no left
    /// the caller storing <c>HEAD~1</c>, which is a different commit tomorrow.</para>
    /// </remarks>
    private static async Task<string?> CommitOfAsync(
        GitCommandRunner git, string token, CancellationToken ct)
    {
        // Nothing user-typed reaches a shell here — GitCommandRunner starts the process directly — but
        // a token opening with a dash would be read by git as an option rather than a ref, so it is
        // refused rather than passed on.
        if (token.Length == 0 || token.StartsWith('-')) return null;

        try
        {
            var answer = await git.RunAsync(
                $"--no-optional-locks rev-parse --verify --quiet {token}^{{commit}}", throwOnError: false, ct);
            var commit = answer.Trim();
            return commit.Length > 0 ? commit : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
