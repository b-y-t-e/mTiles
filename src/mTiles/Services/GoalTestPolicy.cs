using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Which step of a goal run has the project's tests run, apart from the loop that carries it out.
/// <para>Pure and separate for the reason <see cref="GoalReviewGatePolicy"/> is: the loop needs an AI
/// process and a git worktree to turn over once, and "does this lap run the tests" would otherwise be
/// a condition written inline in three places that could disagree.</para>
/// </summary>
internal static class GoalTestPolicy
{
    /// <summary>
    /// Whether the implementation is told to leave the tests passing and check that itself.
    /// </summary>
    /// <remarks>Only where every review checks them too. Under a deferred timing the implementer is
    /// told the suite is run later instead — and is still allowed the tests a finding names, which is
    /// what the lap after a failed test run is for.</remarks>
    public static bool InImplement(GoalCompletionCriteria criteria) =>
        criteria.RequireTestsPass && criteria.TestTiming == GoalTestTiming.EveryReview;

    /// <summary>Whether this lap's review runs the tests.</summary>
    /// <param name="attempt">The attempt the review judges, counted from 1.</param>
    public static bool InReview(GoalCompletionCriteria criteria, int attempt) =>
        criteria.RequireTestsPass && criteria.TestTiming switch
        {
            GoalTestTiming.EveryReview => true,
            GoalTestTiming.FirstReviewAndWhenMet => attempt <= 1,
            _ => false,
        };

    /// <summary>
    /// Whether a review that has just accepted the work still owes the goal a test run before it may
    /// count as met.
    /// </summary>
    /// <param name="reviewRanTests">Whether that review was itself told to run them — the first review
    /// under <see cref="GoalTestTiming.FirstReviewAndWhenMet"/>, which has already answered the
    /// question a second run would ask. Never owed under <see cref="GoalTestTiming.EveryReview"/>,
    /// whose every review runs them — the flag is not persisted, so a restart must not read its
    /// absence as a debt.</param>
    public static bool OwesTestRun(GoalCompletionCriteria criteria, bool reviewRanTests) =>
        criteria.RequireTestsPass
        && criteria.TestTiming != GoalTestTiming.EveryReview
        && !reviewRanTests;

    /// <summary>The labels the picker offers, in the order it offers them.</summary>
    public static IReadOnlyList<string> Labels { get; } =
        Enum.GetValues<GoalTestTiming>().Select(Label).ToList();

    /// <summary>What a timing is called on screen.</summary>
    public static string Label(GoalTestTiming timing) => timing switch
    {
        GoalTestTiming.WhenMet => "when done",
        GoalTestTiming.FirstReviewAndWhenMet => "first review + when done",
        _ => "every review",
    };

    /// <summary>The sentence under a row of the picker.</summary>
    public static string Description(GoalTestTiming timing) => timing switch
    {
        GoalTestTiming.WhenMet =>
            "Only once a review accepts the work. A failure sends the run round again, and the next " +
            "acceptance runs them again.",
        GoalTestTiming.FirstReviewAndWhenMet =>
            "On the first review, to catch a change that breaks the suite early, and again once a " +
            "review accepts the work.",
        _ => "The implementation keeps them passing and every review checks it. The most thorough, " +
             "and the dearest.",
    };

    /// <summary>The timing a label names, falling back to the default rather than throwing — the
    /// rule <see cref="GoalReviewGatePolicy.FromLabel"/> follows, and for the same reason.</summary>
    public static GoalTestTiming FromLabel(string? label) =>
        Enum.GetValues<GoalTestTiming>().FirstOrDefault(
            t => string.Equals(Label(t), label, StringComparison.OrdinalIgnoreCase),
            GoalTestTiming.EveryReview);
}
