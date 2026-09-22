namespace mTiles.Models;

/// <summary>
/// What the loop does when a review has come back and the goal is not finished.
/// </summary>
/// <remarks>
/// <para>Without this the implement/review loop hands its own findings straight back to the tool and
/// carries on, so the one moment a person can say "that one is not worth fixing" — the moment the list
/// is on screen and nothing has been re-implemented over it yet — passes in the time it takes to read
/// the first finding. What the gate buys is that moment, and the pick it is there for: every finding
/// arrives ticked, and what stays ticked is what goes back to the tool.</para>
/// <para><see cref="Countdown"/> is first so that a goal file written before this existed reads as the
/// default rather than as <see cref="Off"/>. That is a deliberate change to how an old goal behaves —
/// it now waits a few seconds after each review — and it is the whole point of the feature: a gate
/// nobody has switched on is a gate nobody uses.</para>
/// </remarks>
public enum GoalReviewGateMode
{
    /// <summary>Show the findings with the countdown running, and carry on by itself when it expires.
    /// Touching a tick stops the clock — see <see cref="mTiles.Services.GoalReviewGatePolicy"/>.</summary>
    Countdown,

    /// <summary>Stop after every review and wait for Resume. The countdown's other end: nothing moves
    /// until somebody says so.</summary>
    Manual,

    /// <summary>Straight on to the next attempt, as the loop behaved before any of this existed.</summary>
    Off,
}
