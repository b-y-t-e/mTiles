namespace mTiles.Models;

/// <summary>
/// Why the implement/review loop stopped, in the terms the summary is written in.
/// <para>In Models rather than beside the policy that produces it because it is persisted: the Summary
/// has to know, after a restart, whether it is looking at a run that ran out of attempts — the one
/// stop that Continue can do anything about — or at one that stopped because carrying on was
/// pointless.</para>
/// </summary>
public enum GoalStopReason
{
    /// <summary>The criteria were met.</summary>
    Met,

    /// <summary>The attempts ran out with the criteria still unmet.</summary>
    BudgetSpent,

    /// <summary>Two consecutive reviews found exactly the same things.</summary>
    NoProgress,

    /// <summary>An implementation left the working tree exactly as it found it.</summary>
    NoChange,

    /// <summary>
    /// A review was asked for on its own, and it has been given. Nothing was implemented and nothing
    /// was going to be.
    /// </summary>
    /// <remarks>
    /// Its own reason rather than one of the four above, because the summary is the sentence the user
    /// reads and none of them is true here. <c>BudgetSpent</c> comes closest and says "stopped after 1
    /// attempt without meeting the completion criteria", which reports a budget running out where no
    /// budget was ever in play — and offers Continue as though attempts had been lost. Continue is
    /// offered here too, and means something different: not more attempts, but the first ones.
    /// </remarks>
    Reviewed,
}
