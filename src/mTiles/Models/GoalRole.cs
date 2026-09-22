namespace mTiles.Models;

/// <summary>
/// What a Goal tile's AI call is <em>for</em>, as opposed to which phase the tile is in.
/// </summary>
/// <remarks>
/// <para>Four jobs, and each wants a different agent and a different amount of thinking. Deliberately
/// <b>not</b> four more members on <see cref="GoalPhase"/>: that enum is written into every goal file,
/// decides <see cref="AiUsage"/> — which is to say what an agent is <em>allowed</em> to do — and gates
/// <c>GoalTilePolicy.CanResume</c>, so adding a member to it is a change to the persisted format and to
/// the permission table for one row of a lookup. This is derived from the phase
/// (<c>GoalRoles.For</c>) and persisted nowhere.</para>
/// <para><see cref="Commit"/> is the one that has no phase of its own: the commit plan is asked for
/// during whatever phase the run happens to be in, so its role is named at the call site rather than
/// worked out. It is also the one with no agent slot and no effort setting — deciding which files
/// belong in which commit is mechanical, and a second opinion buys nothing there.</para>
/// </remarks>
public enum GoalRole
{
    /// <summary>Working the goal out, asking the clarifying questions, writing the plan, summarising.
    /// Nothing here writes to the repository.</summary>
    Planning,

    /// <summary>Carrying the plan out. The one role that edits the working tree.</summary>
    Work,

    /// <summary>Judging what the work produced.</summary>
    Review,

    /// <summary>Deciding which of the changed files belong in which commit, and what to call it.</summary>
    Commit,
}
