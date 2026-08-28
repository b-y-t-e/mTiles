using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// What a proposed set of commits is allowed to be, and how it is described to the person approving it.
/// </summary>
/// <remarks>
/// Pure and separate from <see cref="GoalCommitter"/>, which runs git, for the reason
/// <see cref="GoalCompletionPolicy"/> is separate from the loop: the interesting part is what happens
/// when the tool's answer does not match the question, and none of that should need a repository on
/// disk to exercise.
/// </remarks>
internal static class GoalCommitPlan
{
    /// <summary>
    /// The plan with everything the tool should not have said taken out of it.
    /// </summary>
    /// <remarks>
    /// <para><b>Paths outside the scope are dropped, not committed.</b> The scope is what this run
    /// changed and had a right to change; anything else in the working tree is the user's, and
    /// <c>git commit -- path</c> takes the whole file rather than the part this run wrote. A single
    /// invented path is therefore somebody's unfinished work landing in a commit about something
    /// else.</para>
    /// <para><b>A file named twice is committed once.</b> The second mention is dropped rather than the
    /// whole plan refused: git would commit it with the first block and leave the second empty, so the
    /// only question is whether the user finds that out from a git error or from a commit list that
    /// already accounts for it.</para>
    /// <para><b>Files nothing claimed are added to a final chore.</b> Losing them silently is the worst
    /// available outcome — the run's work is then split between the history and the working tree with
    /// nothing saying so — and asking the tool again would spend another run on the same question.</para>
    /// <para><b>But a plan that claimed nothing at all is refused, not swept.</b> The sweep exists to
    /// catch what one usable plan forgot; applied to no plan it turned an unparseable answer — or one
    /// naming only files this run had no right to touch — into a single commit of everything the run
    /// produced, under a subject nobody wrote. Answering with nothing lets the caller say so, which is
    /// what the message about a tool that "did not come back with a usable set of commits" was written
    /// for and could never reach.</para>
    /// </remarks>
    public static List<GoalCommit> Sound(IReadOnlyList<GoalCommit> planned, GoalCommitScope scope)
    {
        var allowed = new HashSet<string>(scope.Files, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sound = new List<GoalCommit>();

        foreach (var commit in planned)
        {
            // Separators normalised before the comparison, and only on the tool's side. Git speaks
            // forward slashes everywhere including Windows, so the scope — which comes from
            // `diff --name-only` — is already in that alphabet; a model writing about a Windows
            // checkout is under no such rule and comes back with `src\Cart.cs` often enough. Compared
            // as bytes, every one of those paths failed to match, the whole plan was discarded as
            // invented, and the run's work went into the sweeping chore with no grouping at all — a
            // failure that looks like the tool ignoring the instructions rather than like a slash.
            var files = commit.Files
                .Select(Slashed)
                .Where(f => allowed.Contains(f) && used.Add(f))
                .ToList();
            if (files.Count == 0) continue;

            sound.Add(new GoalCommit { Type = commit.Type, Subject = commit.Subject, Files = files });
        }

        // Nothing survived. There is no plan here to complete, so there is nothing to complete it
        // with — see the remark above.
        if (sound.Count == 0) return sound;

        var unclaimed = scope.Files.Where(f => !used.Contains(f)).ToList();
        if (unclaimed.Count > 0)
            sound.Add(new GoalCommit
            {
                Type = "chore",
                Subject = "remaining changes from this goal",
                Files = unclaimed,
            });

        return sound;
    }

    /// <summary>
    /// Everything in scope as one commit, for when there is no usable plan to complete.
    /// </summary>
    /// <remarks>
    /// The last resort, and deliberately dull: a chore naming the goal it came from. It is what is left
    /// when the tool could not answer at all, or answered about files this run has no right to touch,
    /// or when the file list is longer than the tool's command line will carry — a case no retry
    /// improves. One commit of reviewed work beats leaving it in the tree with nothing said.
    /// </remarks>
    public static List<GoalCommit> SweepAll(GoalCommitScope scope) =>
        scope.Files.Count == 0
            ? []
            : [new GoalCommit
            {
                Type = "chore",
                Subject = "changes from this goal",
                Files = [..scope.Files],
            }];

    /// <summary>Git's own spelling of a path, whatever the tool wrote.</summary>
    private static string Slashed(string path) => path.Replace('\\', '/');

    /// <summary>
    /// What the user is asked to approve: the commits, the files kept back, and what the review left
    /// outstanding.
    /// </summary>
    /// <remarks>
    /// The outstanding findings are in here because this is the moment they matter. The run stopped
    /// with them unfixed — that is allowed, the offer needs no blockers and no errors, not a clean
    /// review — and a commit is exactly when somebody should decide whether shipping three warnings is
    /// all right. In the transcript they had already scrolled past.
    /// </remarks>
    /// <param name="existingWork">
    /// True where the goal was worked out from the tree rather than typed. The offer then covers
    /// everything that was uncommitted when the run finished — which is the honest consequence of
    /// having said "these changes are the goal", and has to be said out loud rather than left to be
    /// discovered in <c>git log</c>.
    /// </param>
    public static string Describe(IReadOnlyList<GoalCommit> commits, GoalCommitScope scope,
        int warnings, int suggestions, bool existingWork = false)
    {
        var text = new StringBuilder();

        // Said before the list, because both of these change what the list *means* — and they are
        // written as one decision because they used to be two. Independently, a detected goal with no
        // closing snapshot got both: one line calling the list the tree "when this run finished", the
        // next saying nothing had recorded how the tree looked when it finished. The first is exactly
        // what is unknown in that state, so it is the one that gives way.
        if (existingWork && scope.Bounded)
            text.Append("This goal was worked out from the changes already in the tree, so what is " +
                        "committed below is everything that was uncommitted when this run " +
                        "finished.\n\n");

        if (!scope.Bounded)
            text.Append(
                (existingWork
                    ? "This goal was worked out from the changes already in the tree, and nothing " +
                      "recorded how that tree looked when this run finished — so this is everything " +
                      "uncommitted right now. "
                    : "Nothing recorded how the tree looked when this run finished, so this is " +
                      "everything that has changed since the goal started. ")
                + "Work done by another Goal tile, or by you, cannot be told apart from this run's " +
                "here — read the list before agreeing.\n\n");

        text.Append(Count(commits.Count, "commit")).Append(" from ")
            .Append(Count(commits.Sum(c => c.Files.Count), "file")).Append(":\n\n");

        foreach (var commit in commits)
            text.Append("• ").Append(commit.Message)
                .Append("  (").Append(Count(commit.Files.Count, "file")).Append(")\n");

        var outstanding = new List<string>(2);
        if (warnings > 0) outstanding.Add(Count(warnings, "warning"));
        if (suggestions > 0) outstanding.Add(Count(suggestions, "suggestion"));
        if (outstanding.Count > 0)
            text.Append("\nThe last review left ").Append(string.Join(" and ", outstanding))
                .Append(" unfixed.\n");

        // Named, because a commit takes the whole file: these are files this run also touched, and
        // committing one would carry the user's own unfinished edit along with it.
        Held(text, scope.LeftAlone, "you had already changed before this goal started");

        // The other end of the same rule, and the one a workspace with three Goal tiles in it meets:
        // this run wrote these files and somebody has written them again since, so what would go in
        // is not only this run's work.
        Held(text, scope.TouchedSince, "changed after this run finished");

        return text.Append("\nCommit?").ToString();
    }

    /// <summary>What the transcript records afterwards, including how to take it back.</summary>
    public static string Made(IReadOnlyList<GoalCommit> commits, int made, GoalCommitScope scope)
    {
        if (made == 0) return "Nothing was committed.";

        var text = new StringBuilder();
        text.Append("Committed ").Append(Count(made, "change")).Append(":\n");
        foreach (var commit in commits.Take(made))
            text.Append("  ").Append(commit.Message).Append('\n');

        Held(text, scope.LeftAlone, "you had already changed before this goal started");
        Held(text, scope.TouchedSince, "changed after this run finished");

        // The way back, spelled out rather than assumed. Undoing a commit somebody else made is the
        // one git operation people reach for under time pressure and get wrong, and --soft is what
        // keeps the work in the tree while the commits go away.
        return text.Append("\nTo undo: git reset --soft HEAD~").Append(made).ToString();
    }

    /// <summary>
    /// What the transcript says when the run has nothing of its own to commit.
    /// </summary>
    /// <remarks>
    /// <para><b>Why, not just that.</b> "There is nothing here this run can claim as its own" on its own
    /// reads as a statement about the user's work — as if the tool had done nothing — when what has
    /// usually happened is that every file it touched is also somebody else's: theirs before the goal
    /// started, or another Goal tile's since it finished. The scope knows which files and under which
    /// of the two reasons, and this is the only place the user would ever be told.</para>
    /// <para>The closing snapshot made this state markedly more likely rather than introducing it:
    /// <c>TouchedSince</c> is empty by construction without one, so before it every held file was held
    /// as pre-existing work. Two Goal tiles over the same files is the case it exists for and the case
    /// this message is read in.</para>
    /// </remarks>
    public static string Nothing(GoalCommitScope scope)
    {
        var text = new StringBuilder("There is nothing here this run can claim as its own to commit.");

        Held(text, scope.LeftAlone, "you had already changed before this goal started");
        Held(text, scope.TouchedSince, "changed after this run finished");

        return text.ToString();
    }

    /// <summary>
    /// One list of files this run wrote and will not commit, and the reason it will not.
    /// </summary>
    /// <remarks>
    /// Shared by both reasons rather than written out twice: they differ only in the clause, and the
    /// second one was added by a bug where a workspace with three Goal tiles in it committed all three
    /// runs under the first one's messages. A copy of this paragraph would have been the place that
    /// still said "before this goal started" about work that arrived after it.
    /// </remarks>
    private static void Held(StringBuilder text, IReadOnlyList<string> files, string because)
    {
        if (files.Count == 0) return;

        text.Append('\n').Append(Count(files.Count, "file")).Append(' ').Append(because)
            .Append(' ').Append(files.Count == 1 ? "is" : "are").Append(" left uncommitted:\n")
            .Append(string.Join("\n", files.Select(f => "  " + f)))
            .Append('\n');
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
