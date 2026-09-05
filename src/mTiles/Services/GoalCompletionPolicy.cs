using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Whether a goal is finished, and what to say about it.
/// <para>Pure and beside <see cref="GoalLoopPolicy"/> for the same reason: this used to be the
/// expression <c>reviewResponse.Contains("VERDICT: PASS")</c> written inline in a loop that needs an AI
/// process and a git worktree to turn over once, so the three ways it was wrong — a review that says it
/// <em>cannot</em> pass yet, one that writes "PASSED", and one that finds no bugs in an implementation
/// of the wrong thing — were only ever discovered by watching it happen.</para>
/// </summary>
internal static class GoalCompletionPolicy
{
    /// <summary>
    /// Whether this review satisfies the criteria.
    /// </summary>
    public static bool IsMet(GoalReviewResult review, GoalCompletionCriteria criteria)
    {
        // The verdict is not optional for a review that came back as prose, whatever the criteria say.
        // Findings only exist for a structured one, so with them empty every count below passes on any
        // answer at all: turning the requirement off in front of a tool that ignores the schema removed
        // *every* gate at once, and the goal finished on its first attempt over a review that said it
        // had failed. The setting relaxes one criterion among several; it cannot relax to nothing.
        if (!review.WasStructured && !review.GoalMet) return false;

        if (criteria.RequireGoalMet && !review.GoalMet) return false;

        // No threshold, and deliberately none in the panel either: a blocker is the finding that says
        // the change is unacceptable rather than merely wrong, and a tolerance for those would be a
        // setting whose only use is to ship something the reviewer said must not ship.
        if (review.Count(GoalSeverity.Blocker) > 0) return false;
        if (review.Count(GoalSeverity.Error) > Tolerated(criteria.MaxErrors)) return false;
        if (review.Count(GoalSeverity.Warning) > Tolerated(criteria.MaxWarnings)) return false;

        // Suggestions are never counted. There is no threshold for them and no way to set one: a
        // criterion nobody can satisfy by fixing anything is a criterion that only wastes attempts.
        return true;
    }

    /// <summary>
    /// The one line that says why the criteria were not met — the thing the user has to read when a
    /// run stops short, and the thing the transcript is missing when it says only "review found issues".
    /// </summary>
    public static string WhyNotMet(GoalReviewResult review, GoalCompletionCriteria criteria)
    {
        var reasons = new List<string>(4);

        if (!review.GoalMet && (criteria.RequireGoalMet || !review.WasStructured))
            // Told apart, because they call for different things from the user: a tool that answers the
            // question and answers no, and a tool that never addressed it.
            reasons.Add(review.SaidNothingAboutTheGoal
                ? "the review did not say whether the goal is met"
                : "the review says the goal is not met");

        var blockers = review.Findings.Count(f => f.Severity == GoalSeverity.Blocker);
        if (blockers > 0)
            reasons.Add(Count(blockers, "blocker"));

        // Through the same Tolerated as IsMet, or the two disagree: a limit of -1 had this list a
        // reason the run was blocked while IsMet let it through.
        var errors = review.Count(GoalSeverity.Error);
        if (errors > Tolerated(criteria.MaxErrors))
            reasons.Add($"{Count(errors, "error")} (limit {Tolerated(criteria.MaxErrors)})");

        var warnings = review.Count(GoalSeverity.Warning);
        if (warnings > Tolerated(criteria.MaxWarnings))
            reasons.Add($"{Count(warnings, "warning")} (limit {Tolerated(criteria.MaxWarnings)})");

        return reasons.Count == 0 ? "the criteria are not met" : string.Join(", ", reasons);
    }

    /// <summary>
    /// Whether this review found exactly what the one before it found, and the run is therefore going
    /// round in a circle.
    /// <para>Only asked of a <see cref="GoalReviewResult.WasStructured"/> review, and that is the whole
    /// reason this is a method. An unstructured one has no findings to compare, so its fingerprint is
    /// the same two words on every lap — and the check would have cut every run by a tool that ignores
    /// the schema down to two attempts, reporting it as "the last two reviews found the same things"
    /// when nothing had been compared at all.</para>
    /// </summary>
    public static bool RepeatsPrevious(GoalReviewResult review, string? previousFingerprint) =>
        review.WasStructured
        && previousFingerprint != null
        && previousFingerprint == review.Fingerprint();

    /// <summary>How a finished run is summarised, given why it ended.</summary>
    /// <param name="permissionDenials">How many tool calls the last run was refused permission for.
    /// It changes only the <see cref="GoalStopReason.NoChange"/> sentence, and it changes what that
    /// sentence is <em>about</em>: an empty worktree because the tool decided against the work is a
    /// dead end, and an empty worktree because it was never allowed to touch a file is a setting two
    /// clicks away. The old wording said the first of those in both cases.</param>
    public static string Summarise(GoalStopReason reason, int attempts, string? outstanding = null,
        int permissionDenials = 0) =>
        reason switch
    {
        GoalStopReason.Met => $"Goal completed after {Count(attempts, "attempt")}.",

        // Not "goal completed after 5 iterations", which is what this said for years and is the
        // opposite of what happened.
        //
        // The attempts that were actually made, not the budget they came out of. Those are the same
        // number in the ordinary case and stop being so as soon as the budget moves: lowering it from
        // five to two while four attempts had already run reported "stopped after 2 attempts" over a
        // transcript containing four of them.
        // What was outstanding, where the caller knows. The old sentence said only that the criteria
        // were not met, which leaves the one decision this summary exists to inform — more attempts,
        // or a tolerance that admits what the reviewer keeps finding — to be made by reading back
        // through the transcript.
        GoalStopReason.BudgetSpent =>
            $"Stopped after {Count(attempts, "attempt")} without meeting the completion criteria"
            + Outstanding(outstanding),

        // "Found exactly the same things" is untrue of the case where they found nothing at all: two
        // reviews in a row with no findings and the goal unmet fingerprint identically, and the summary
        // then described defects that did not exist. "Reached the same conclusion" is true of both.
        //
        // These two carry what was outstanding as well, for the reason BudgetSpent does: the stop says
        // why the run ended and the outstanding line says what it ended *with*, and the caller knows
        // both by the time either of these is reached.
        GoalStopReason.NoProgress =>
            $"Stopped after {Count(attempts, "attempt")}: two reviews in a row reached the same " +
            "conclusion, so more attempts would reach it again" + Outstanding(outstanding),

        // Two sentences from one stop, because the worktree looks the same either way and the reader's
        // next move does not: one is a goal that has run out of road, the other is a run that was never
        // allowed to do the work and needs the permission mode on the strip changed, not a new goal.
        GoalStopReason.NoChange when permissionDenials > 0 =>
            $"Stopped after {Count(attempts, "attempt")}: the last attempt changed no files because "
            + $"{Count(permissionDenials, "tool call")} {(permissionDenials == 1 ? "was" : "were")} "
            + "refused permission. Set the permission "
            + "mode beside the tool name to auto or higher, then try again"
            + Outstanding(outstanding),

        // What happened, not what would happen next. This said "so the same prompt would change none
        // again", which is a prediction and a false one: the unchanged tree is reviewed before this
        // sentence is written, and those findings go into the next implement prompt — so the next
        // attempt is handed something this one was not. The fact on its own is what the reader needs,
        // because the odd thing here is an agent that had an unmet criterion in front of it and wrote
        // nothing, and that is a thing to look at rather than a budget to top up.
        GoalStopReason.NoChange =>
            $"Stopped after {Count(attempts, "attempt")}: the agent changed no files"
            + StillOutstanding(outstanding),

        // No attempt count: none were spent and none were meant to be. Saying "after 0 attempts" would
        // describe a run that failed to start rather than one that did exactly what was asked.
        GoalStopReason.Reviewed when outstanding is { Length: > 0 } =>
            $"Reviewed the working tree against the goal: {outstanding}.",

        GoalStopReason.Reviewed => "Reviewed the working tree against the goal: nothing outstanding.",

        _ => $"Stopped after {Count(attempts, "attempt")}.",
    };

    /// <summary>
    /// How many attempts a run actually gets, whatever the criteria say.
    /// <para>The panel stores what was typed and a saved file can hold anything, so the bound lives
    /// where the number is used — and in one place, because the loop and the summary both need it and a
    /// second copy of the arithmetic is how the summary came to report attempts that never happened.
    /// One is the least a run can be worth: zero would put a goal into Summary the moment its plan was
    /// approved, having done nothing.</para>
    /// </summary>
    public static int Attempts(GoalCompletionCriteria criteria) =>
        Math.Clamp(criteria.MaxIterations, 1, MostAttempts);

    /// <summary>The ceiling <see cref="Attempts"/> clamps to. Named because Continue has to ask whether
    /// there is any room left above the attempts already spent: a button that raises a budget already
    /// at the ceiling would run the loop once round to no next attempt and summarise again.</summary>
    public const int MostAttempts = 100;

    /// <summary>What was left unresolved, where the caller knows it, as the tail of a sentence.</summary>
    private static string Outstanding(string? outstanding) =>
        outstanding is { Length: > 0 } ? $": {outstanding}." : ".";

    /// <summary>The same, as a sentence of its own.</summary>
    /// <remarks>
    /// <para>Not <see cref="Outstanding"/>, which continues the sentence after a colon this one has
    /// already spent: "the agent changed no files: 1 warning (limit 0)" reads as two unrelated facts,
    /// and the whole point of this stop is that they are in tension — the criterion was unmet and the
    /// agent wrote nothing anyway.</para>
    /// <para>A separate sentence rather than an "although" clause, because
    /// <see cref="WhyNotMet"/> answers with whole phrases as readily as with counts — "the review says
    /// the goal is not met" — and only one of the two shapes fits inside a clause.</para>
    /// </remarks>
    private static string StillOutstanding(string? outstanding) =>
        outstanding is { Length: > 0 } ? $". Still outstanding: {outstanding}." : ".";

    /// <summary>Negative tolerances read as zero. They come from a text box and from a file, and
    /// "fewer than no errors" is not a criterion anybody can meet.</summary>
    private static int Tolerated(int limit) => Math.Max(0, limit);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
