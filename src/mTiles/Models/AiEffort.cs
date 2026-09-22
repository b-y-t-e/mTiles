namespace mTiles.Models;

/// <summary>
/// How hard the AI tool is asked to think, for the runs this tile starts.
/// </summary>
/// <remarks>
/// <para><b>A Goal run no longer picks one of these for the whole run</b>, and the argument that used
/// to stand here — the budget is in attempts, so a cheap attempt costs as much of it as a careful one,
/// hence <see cref="High"/> everywhere — is superseded by
/// <c>docs/adr/0003-effort-by-role-in-a-goal-run.md</c>. It holds for planning and reviewing and not
/// for carrying a plan out, so the tile asks per <c>GoalRole</c> through <c>GoalRoles.EffortFor</c>;
/// what is left here is the scale itself.</para>
/// <para>The Goal tile's own setting is in <c>settings.json</c> rather than in the goal file, the same
/// choice <see cref="AiBehaviour"/> makes and for a related reason: goal files live in
/// <c>.mtiles/goals/</c> inside the user's repository and travel with a branch, and how hard somebody's
/// own machine should think is not a property of the branch.</para>
/// <para>The levels are <c>claude --effort</c>'s own spellings. Measured: an unrecognised
/// <em>value</em> is forgiving — the tool warns and carries on with its default — but an unrecognised
/// <em>flag</em> is not, and an older Claude Code answers <c>error: unknown option '--effort'</c> and
/// runs nothing at all. That is the same trap <c>AiBehaviours</c> was built around, which is why
/// <see cref="ToolDefault"/> exists here too and why the rejection is recognised by name.</para>
/// </remarks>
public enum AiEffort
{
    /// <summary>Thinks it through. What the Goal tile's <c>thorough</c> preset buys its planning and
    /// its review; declared first because it was once the whole tile's default.</summary>
    High,

    Low,

    Medium,

    /// <summary>Above the tool's own maximum for interactive use. Offered because a run nobody is
    /// watching is exactly where it can be afforded.</summary>
    XHigh,

    Max,

    /// <summary>No flag at all — whatever the tool's own configuration says. The way out for a machine
    /// whose Claude Code predates <c>--effort</c>, where any value at all stops every run.</summary>
    ToolDefault,
}
