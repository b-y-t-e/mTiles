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
    /// Whether this review, and the verification that ran before it, satisfy the criteria.
    /// </summary>
    /// <param name="verify">The result of the user's verify command, or null when there is none. A
    /// non-zero exit is a hard gate ahead of everything else: it is the only fact here that is not the
    /// tool's opinion of its own work, and a review declaring success over a build that does not
    /// compile is exactly the outcome this exists to catch.</param>
    public static bool IsMet(GoalReviewResult review, VerifyOutcome? verify, GoalCompletionCriteria criteria)
    {
        if (verify is { Ran: true, Succeeded: false }) return false;

        // A verification that never finished has not passed. It is not the same as one that could not be
        // started — that is the machine's fault and is deliberately forgiven — and treating them alike
        // meant a hung build was no obstacle to "goal completed".
        if (verify is { TimedOut: true }) return false;

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
    public static string WhyNotMet(GoalReviewResult review, VerifyOutcome? verify, GoalCompletionCriteria criteria)
    {
        var reasons = new List<string>(4);

        if (verify is { Ran: true, Succeeded: false } v)
            reasons.Add($"the verify command exited {v.ExitCode}");
        if (verify is { TimedOut: true })
            reasons.Add("the verify command never finished");
        if (!review.GoalMet && (criteria.RequireGoalMet || !review.WasStructured))
            // Told apart, because they call for different things from the user: a tool that answers the
            // question and answers no, and a tool that never addressed it.
            reasons.Add(review.SaidNothingAboutTheGoal
                ? "the review did not say whether the goal is met"
                : "the review says the goal is not met");

        var blockers = review.Count(GoalSeverity.Blocker);
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
    public static bool RepeatsPrevious(GoalReviewResult review, string? previousFingerprint,
        GoalCompletionCriteria criteria) =>
        criteria.StopOnNoProgress
        && review.WasStructured
        && previousFingerprint != null
        && previousFingerprint == review.Fingerprint();

    /// <summary>How a finished run is summarised, given why it ended.</summary>
    public static string Summarise(GoalStopReason reason, int attempts, string? outstanding = null) =>
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
            $"Stopped after {Count(attempts, "attempt")}: the last two reviews reached exactly the same " +
            "conclusion, so the remaining attempts would repeat it" + Outstanding(outstanding),

        GoalStopReason.NoChange =>
            $"Stopped after {Count(attempts, "attempt")}: the last implementation changed nothing in the " +
            "working tree" + Outstanding(outstanding),

        // Stopped rather than tried again. The timeout is already half an hour, so the attempts left
        // are hours of waiting for the same answer — and the answer is unusable either way, because a
        // verification that never finished says nothing about whether the goal is met.
        GoalStopReason.VerifyTimedOut =>
            $"Stopped after {Count(attempts, "attempt")}: the verify command never finished and had to " +
            "be stopped. Check that it does not wait for input, or clear it under the tune button — " +
            "clearing it offers Continue, so this goal can carry on without being retyped.",

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
    public const int MostAttempts = 50;

    /// <summary>What was left unresolved, where the caller knows it, as the tail of a sentence.</summary>
    private static string Outstanding(string? outstanding) =>
        outstanding is { Length: > 0 } ? $": {outstanding}." : ".";

    /// <summary>Negative tolerances read as zero. They come from a text box and from a file, and
    /// "fewer than no errors" is not a criterion anybody can meet.</summary>
    private static int Tolerated(int limit) => Math.Max(0, limit);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
