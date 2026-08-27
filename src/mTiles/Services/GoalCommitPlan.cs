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
    /// <em>everything</em> uncommitted, including anything the user has changed since — which is the
    /// honest consequence of having said "these changes are the goal", and has to be said out loud
    /// rather than left to be discovered in <c>git log</c>. A tile reopened days later is exactly the
    /// case: the run it is offering to commit is old and the working tree is not.
    /// </param>
    public static string Describe(IReadOnlyList<GoalCommit> commits, GoalCommitScope scope,
        int warnings, int suggestions, bool existingWork = false)
    {
        var text = new StringBuilder();

        if (existingWork)
            text.Append("This goal was worked out from the changes already in the tree, so what is " +
                        "committed below is everything uncommitted here now — including anything you " +
                        "have changed since.\n\n");
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
        if (scope.LeftAlone.Count > 0)
            text.Append('\n').Append(Count(scope.LeftAlone.Count, "file"))
                .Append(" you had already changed before this goal started ")
                .Append(scope.LeftAlone.Count == 1 ? "is" : "are")
                .Append(" left uncommitted:\n")
                .Append(string.Join("\n", scope.LeftAlone.Select(f => "  " + f)))
                .Append('\n');

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

        if (scope.LeftAlone.Count > 0)
            text.Append("\nLeft alone, because you had already changed ")
                .Append(scope.LeftAlone.Count == 1 ? "it" : "them")
                .Append(" before this goal started:\n")
                .Append(string.Join("\n", scope.LeftAlone.Select(f => "  " + f)))
                .Append('\n');

        // The way back, spelled out rather than assumed. Undoing a commit somebody else made is the
        // one git operation people reach for under time pressure and get wrong, and --soft is what
        // keeps the work in the tree while the commits go away.
        return text.Append("\nTo undo: git reset --soft HEAD~").Append(made).ToString();
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
