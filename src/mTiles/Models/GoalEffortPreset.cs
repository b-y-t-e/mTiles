namespace mTiles.Models;

/// <summary>
/// How hard a Goal run thinks, said once for the whole run rather than once per role.
/// </summary>
/// <remarks>
/// <para>A preset and not three pickers, and that is a decision about the strip rather than about the
/// type. Effort is worth setting per role — the evidence is that planning and reviewing repay it and
/// implementing largely does not — but three choosers in a strip that already carries an agent, a
/// permission mode and a status is a panel for flying an aeroplane, in a tile that is often 300px wide.
/// One word in the strip, the three levels spelled out in the row's own description where somebody
/// opening the list can read them.</para>
/// <para><see cref="GoalRole.Commit"/> is in none of them. It is a constant — see
/// <c>GoalRoles.CommitEffort</c> — so no preset, and no field anywhere, can set it wrong.</para>
/// </remarks>
public enum GoalEffortPreset
{
    /// <summary>The default: think about the goal and the review, get on with the work.</summary>
    Balanced,

    /// <summary>More of everything, for work that has already been got wrong once.</summary>
    Thorough,

    /// <summary>Everything at its cheapest.</summary>
    Cheap,

    /// <summary>No effort flag at all, whatever the role — the way out on a CLI older than the
    /// option.</summary>
    ToolDefault,
}
