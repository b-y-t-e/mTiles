namespace mTiles.Models;

/// <summary>
/// How much an AI agent may do without stopping to ask — <b>one canonical vocabulary</b>, mapped to
/// each agent's own flags by its <c>IAiAgent</c>.
/// </summary>
/// <remarks>
/// <para>Canonical rather than one agent's spelling, because there is no spelling all five share:
/// claude has six modes behind <c>--permission-mode</c>, opencode has a boolean <c>--auto</c>, codex
/// has two orthogonal axes (<c>--sandbox</c> × <c>-a</c>), agy has <c>--mode</c> plus a bypass switch,
/// and pi has no gate at all. Mapping <b>by meaning, never by spelling</b> is what keeps opencode's
/// <c>--auto</c> — "auto-approve permissions that are not explicitly denied (dangerous!)" — where it
/// belongs, which is <see cref="BypassPermissions"/> and not <see cref="Auto"/>.</para>
/// <para>How much each one lets an agent do is <c>AiBehaviours.Strength</c>'s to say, not this
/// declaration's: the members are in the order they were written, because they are a file format and
/// the two newest could not be inserted in the middle of one. That ranking is load-bearing — an agent
/// that does not support the mode asked of it rounds <b>down</b>, never up
/// (<c>AiBehaviours.RoundDown</c>). Falling to a weaker mode costs a run that stops to ask about
/// something; rounding up would hand somebody's repository to an unattended agent they never
/// authorised.</para>
/// <para>Persisted in <c>settings.json</c> rather than in the goal file, and by <em>member name</em>:
/// goal files live in <c>.mtiles/goals/</c> inside the user's repository and travel with a branch, so
/// a stored <see cref="BypassPermissions"/> would be a checked-in instruction to run somebody else's
/// agent unattended. The spellings here are therefore a file format — <see cref="BypassPermissions"/>
/// keeps claude's word rather than the canonical label "bypass" because changing it would read as an
/// unknown value on every settings file already written, and the tolerant converter answers those with
/// the default.</para>
/// </remarks>
public enum AiBehaviour
{
    /// <summary>Claude Code's own <c>auto</c> mode, and the default here: it gets on with the work and
    /// still stops at what it considers dangerous.</summary>
    Auto,

    /// <summary>Edits go through unasked, everything else follows the agent's normal rules.</summary>
    /// <remarks>Deliberately <em>not</em> offered for a headless Goal run: it still asks for every
    /// non-edit tool, and in a run with nobody to ask that is a silent denial — worse than either
    /// neighbour, because a transcript full of them looks like an agent that decided to do nothing.
    /// </remarks>
    AcceptEdits,

    /// <summary>Nothing is asked about at all. Named plainly rather than hidden behind a word like
    /// "fast", because a goal loop left alone for hours is exactly where it gets used.</summary>
    BypassPermissions,

    /// <summary>No flag at all — whatever the agent's own configuration says. What the Goal tile did
    /// before the setting existed, and the way out for a version of an agent that has never heard of
    /// the flag this would otherwise pass.</summary>
    ToolDefault,

    /// <summary>Read the repository, write nothing. What the plan and review phases run as, chosen by
    /// the agent rather than by the user — neither phase needs write access, and a review agent that
    /// can write is a second agent editing a worktree <c>GoalBaseline</c> only photographed once.
    /// </summary>
    Plan,

    /// <summary>Every tool call is put to a human first. The weakest thing an agent can be asked to do
    /// short of passing no flag, and unusable headlessly for the reason
    /// <see cref="AcceptEdits"/> gives.</summary>
    Ask,
}
