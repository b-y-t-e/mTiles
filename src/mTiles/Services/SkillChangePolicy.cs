namespace mTiles.Services;

/// <summary>What a tile holding a running agent should do when this workspace's skills change.</summary>
public enum SkillChangeResponse
{
    /// <summary>Nothing. Either the agent follows the change itself, or it has not started yet and will
    /// read the skill when it does.</summary>
    Nothing,

    /// <summary>Restart the agent without asking: it is idle and nothing unsent would go with it.</summary>
    Restart,

    /// <summary>Say that a restart is needed, and leave the decision to the user.</summary>
    Tell,
}

/// <summary>
/// Whether a skill written while an agent is running is worth acting on, and how.
/// </summary>
/// <remarks>
/// <para>Ticking a database in the Database tile writes <c>SKILL.md</c> into the skills directories of the
/// agents this workspace holds. No agent is trusted to re-read it
/// (<see cref="Agents.IAiAgent.WatchesSkillsDirectory"/>); every one of them finds out when the process is
/// started again. Until now nothing said so, so the user's databases were invisible to the agent they had
/// just granted them to, with no sign anywhere.</para>
/// <para><b>An idle agent is restarted, whatever the conversation holds.</b> The restart itself is cheap —
/// the transcript is event-sourced and cannot be lost — and the one cost that used to stop this was what
/// came after it: a cold resume that failed in silence, leaving the agent with no memory of a conversation
/// still shown in full. Measured live 2026-09-17, none of the six is silent any more. Claude Code, codex
/// and opencode always said so; Grok 1.0.34 advertises <c>loadSession</c> and answers an unknown id with a
/// JSON-RPC error the ACP session turns into a notice; and pi and agy, which did fail quietly, are now
/// caught before the first message by <c>ResumeCheck</c> — pi's <c>messageCount</c>, agy's <c>init</c>
/// naming a different conversation. So the worst a restart does is lose the agent's memory <i>and say
/// so</i>, which is a cost the user can see and act on rather than one they discover from an answer that
/// makes no sense.</para>
/// <para><b>What still stops it</b> is what a restart would take that nothing can put back: a turn in flight
/// or a question half answered (<paramref name="isBusy"/>), and a message typed and not yet sent
/// (<paramref name="hasUnsentWork"/>). Those get the notice instead.</para>
/// <para>Pure, and a policy of its own for the reason <c>ChainPolicy</c> and <c>ActivityPolicy</c> are:
/// the interesting part is which of three answers a situation deserves, and that is easier to argue in a
/// table test than to reconstruct from a tile that did something surprising.</para>
/// </remarks>
public static class SkillChangePolicy
{
    /// <param name="agentFollowsIt">The agent re-reads its skills directory while running.</param>
    /// <param name="isRunning">The agent's process has been started at all.</param>
    /// <param name="isBusy">A turn is in flight, or the agent is waiting for an answer.</param>
    /// <param name="hasUnsentWork">Something is typed or attached and not sent yet.</param>
    public static SkillChangeResponse For(bool agentFollowsIt, bool isRunning, bool isBusy, bool hasUnsentWork)
    {
        // Claude Code watches the directory. Telling the user to restart it would be this application
        // asking for something it has already been given.
        if (agentFollowsIt) return SkillChangeResponse.Nothing;

        // Nothing has started, so nothing has read anything: the skill is on disk before the first read.
        if (!isRunning) return SkillChangeResponse.Nothing;

        // Busy is the one state a restart must never interrupt, whatever else is true — it is a turn in
        // flight, or a question the user is halfway through answering.
        if (isBusy) return SkillChangeResponse.Tell;

        // Something typed and not sent would go with the process. That, and not the conversation above it,
        // is what an idle restart can still lose.
        return hasUnsentWork ? SkillChangeResponse.Tell : SkillChangeResponse.Restart;
    }

    /// <summary>What the user is told, when they are told anything.</summary>
    /// <remarks>It names the cause, because a restart asked for out of nowhere is a restart nobody
    /// performs — and it does not promise that the agent is currently missing the databases, because
    /// nothing here can know that: five of the CLIs say nothing either way.</remarks>
    public const string Notice =
        "Database access for this workspace has changed. Restart the agent for it to see the new skill.";

    /// <summary>
    /// How long a run of skill changes is let settle before anything is restarted for it.
    /// </summary>
    /// <remarks>
    /// <para><b>A change is a click, and clicks come in runs.</b> Every database added in the Database
    /// tile and every RW toggle writes the skill again, so ticking three databases is three changes
    /// seconds apart — and only the last of them describes what the user meant to grant. Restarted on
    /// each, an agent is torn down and started again three times over, of which two are work nobody
    /// asked for: on agy one failed resume alone is measured at 98 s and 282k tokens
    /// (<c>docs/AGENT-CONVERSATIONS.md</c>), so a run of clicks cost minutes and most of a million
    /// tokens to arrive where one restart would have.</para>
    /// <para>Two seconds is the compromise it looks like: long enough to swallow a run of clicks, short
    /// enough that a single change is acted on while the user is still looking at the tile they made it
    /// in. It bounds only how long the tile waits before starting; a change arriving while a restart is
    /// already running is coalesced by the restart itself, however long that takes.</para>
    /// <para>Settable only by the test suite, which shortens it rather than waiting it out.</para>
    /// </remarks>
    public static TimeSpan QuietWindow { get; internal set; } = TimeSpan.FromSeconds(2);
}
