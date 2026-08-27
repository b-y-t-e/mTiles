namespace mTiles.Models;

/// <summary>
/// How hard the AI tool is asked to think, for the runs this tile starts.
/// </summary>
/// <remarks>
/// <para>A Goal run is the case where this is worth spending: the tile is meant to be left alone for
/// an hour on work the user has already decided is worth an hour, and the failure it keeps paying for
/// is an attempt spent on a shallow answer — the budget is in attempts, and a cheap attempt costs
/// exactly as much of it as a careful one. Hence <see cref="High"/> as the default rather than the
/// tool's own, which is tuned for interactive use where a person is watching and can redirect.</para>
/// <para>In settings rather than in the goal file, the same choice <see cref="AiPermissionMode"/>
/// makes and for a related reason: goal files live in <c>.mtiles/goals/</c> inside the user's
/// repository and travel with a branch, and how hard somebody's own machine should think is not a
/// property of the branch.</para>
/// <para>The levels are <c>claude --effort</c>'s own spellings. Measured: an unrecognised
/// <em>value</em> is forgiving — the tool warns and carries on with its default — but an unrecognised
/// <em>flag</em> is not, and an older Claude Code answers <c>error: unknown option '--effort'</c> and
/// runs nothing at all. That is the same trap <c>AiPermissionModes</c> was built around, which is why
/// <see cref="ToolDefault"/> exists here too and why the rejection is recognised by name.</para>
/// </remarks>
public enum AiEffort
{
    /// <summary>The tile's default. A goal run is left alone, and a shallow attempt spends as much of
    /// the budget as a careful one.</summary>
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
