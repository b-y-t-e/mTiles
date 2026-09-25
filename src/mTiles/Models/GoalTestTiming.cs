namespace mTiles.Models;

/// <summary>
/// When a goal run has the project's tests run, where its criteria ask for them at all.
/// </summary>
/// <remarks>
/// <para>The tests are the dearest part of a lap — minutes of wall clock and a page of output the
/// tool reads back — and on the early laps of a goal they are usually paid for twice, by the
/// implementation and by the review, against work the review is about to send back anyway. The two
/// deferred timings spend them where they decide something: on the work the review has accepted.</para>
/// <para><see cref="EveryReview"/> is first so that a goal file written before this existed reads as
/// what that tile did. What each one means lap by lap is <c>GoalTestPolicy</c>.</para>
/// </remarks>
public enum GoalTestTiming
{
    /// <summary>The implementation leaves them passing and every review checks it.</summary>
    EveryReview,

    /// <summary>Only once a review has accepted the work. A failure is a finding like any other, sends
    /// the run round again, and the next acceptance runs them again.</summary>
    WhenMet,

    /// <summary>As <see cref="WhenMet"/>, and on the first review as well — so a change that breaks the
    /// suite early is caught before the attempts after it are built on top of it.</summary>
    FirstReviewAndWhenMet,
}
