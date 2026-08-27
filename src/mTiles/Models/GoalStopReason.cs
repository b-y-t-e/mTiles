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
}
