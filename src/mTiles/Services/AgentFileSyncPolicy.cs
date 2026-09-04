namespace mTiles.Services;

/// <summary>What the wizard should ask, if anything.</summary>
public enum AgentFileSyncWizardMode
{
    /// <summary>Nothing to ask — already answered, sync is off globally, or there is nothing to sync.</summary>
    None,

    /// <summary>A single yes/no: enable mirroring for this workspace.</summary>
    AskEnableOnly,

    /// <summary>Enable, and — because the two files disagree — say which one is right.</summary>
    AskEnableAndPickAuthoritative,
}

/// <summary>
/// Pure decision: given what is on disk and what is in the tile tree, does opening (or changing) this
/// workspace warrant asking about CLAUDE.md/AGENTS.md sync, and what does the question look like.
/// </summary>
/// <remarks>
/// <para>Both files existing is ambiguous whether or not their content agrees — sync is worth asking
/// about either way, but only when the content actually differs is there anything to pick between.</para>
/// <para><paramref name="contentsDiffer"/> is a delegate rather than a value because reading both files
/// is the expensive part of the question and is only ever the answer to it when both exist and nothing
/// has been decided yet — and this is asked again on every tile-tree change, a dragged splitter
/// included.</para>
/// <para>Exactly one file existing is only worth asking about when a tile in this workspace reads the
/// <em>other</em> one — creating the file nobody's tile needs is littering, the same rule
/// <see cref="WorkspaceAgentFiles"/> already follows for skills and shims.</para>
/// </remarks>
public static class AgentFileSyncPolicy
{
    public static AgentFileSyncWizardMode Decide(
        bool claudeExists,
        bool agentsExists,
        Func<bool> contentsDiffer,
        bool needsClaudeStyle,
        bool needsAgentsStyleOnly,
        bool wizardAlreadyAnswered,
        bool globallyEnabled)
    {
        if (wizardAlreadyAnswered || !globallyEnabled)
            return AgentFileSyncWizardMode.None;

        if (claudeExists && agentsExists)
            return contentsDiffer()
                ? AgentFileSyncWizardMode.AskEnableAndPickAuthoritative
                : AgentFileSyncWizardMode.AskEnableOnly;

        if (claudeExists && !agentsExists)
            return needsAgentsStyleOnly ? AgentFileSyncWizardMode.AskEnableOnly : AgentFileSyncWizardMode.None;

        if (agentsExists && !claudeExists)
            return needsClaudeStyle ? AgentFileSyncWizardMode.AskEnableOnly : AgentFileSyncWizardMode.None;

        return AgentFileSyncWizardMode.None;
    }
}
