namespace mTiles.Models;

/// <summary>
/// How much the AI tool may do without stopping to ask, for the runs this tile starts.
/// <para>The tile used to pass nothing at all, so every run inherited whatever mode the user's own
/// Claude Code settings happened to be in. On a machine still at the factory default that is
/// <em>ask</em> — and a headless <c>-p</c> run has nobody to ask, so every edit was refused, the
/// implementation wrote no files, and the tile stopped with "the last attempt changed no files": a
/// message about the wrong thing entirely, from a run that never had permission to do the work.</para>
/// <para>In Models rather than beside the runner because it is persisted (settings.json), and in
/// <em>settings</em> rather than in the goal file on purpose: goal files live in
/// <c>.mtiles/goals/</c> inside the user's repository and travel with a branch, so a stored
/// <see cref="BypassPermissions"/> would be a checked-in instruction to run somebody else's agent
/// unattended — the same hazard the verify command's consent already refuses to persist.</para>
/// </summary>
public enum AiPermissionMode
{
    /// <summary>Claude Code's own <c>auto</c> mode, and the default here: it gets on with the work and
    /// still stops at what it considers dangerous.</summary>
    Auto,

    /// <summary>Edits go through unasked, everything else follows the tool's normal rules.</summary>
    AcceptEdits,

    /// <summary>Nothing is asked about at all. The tile offers it because a goal loop is meant to be
    /// left alone for hours, and names it plainly rather than hiding it behind a word like "fast".
    /// </summary>
    BypassPermissions,

    /// <summary>No flag at all — whatever the tool's own configuration says. What this tile did before
    /// the setting existed, kept for a user whose settings already say something deliberate.</summary>
    ToolDefault,
}
