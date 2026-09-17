using mTiles.AgentSessions.Events;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// How full the model's context is, as a bar can draw it.
/// </summary>
/// <remarks>
/// <para><b>A window nobody named is not a full bar and not an empty one.</b> Only three of the agents
/// report the size of the context they are working in — codex names <c>modelContextWindow</c>, ACP names
/// <c>size</c> — and the rest report the tokens alone. Read as zero, an unreported window would draw
/// every Claude Code conversation as a bar that never moves; read as the tokens themselves it would draw
/// one that is always full. So the answer is <c>null</c>, the bar is not drawn at all, and the figures
/// beside it still say what was spent.</para>
/// <para>Pure, and a type of its own for the reason <c>UsagePace</c> is one: it is the arithmetic behind
/// something on screen, and arithmetic in a view is arithmetic nobody can argue with in a test.</para>
/// </remarks>
public static class ContextGauge
{
    /// <summary>How much of the window is gone, 0 to 100, or null when it cannot be said.</summary>
    /// <remarks>Clamped at both ends. Over 100 happens — an agent that compacts reports the tokens it
    /// had before the compaction landed — and a bar drawn past its own end is a rendering artefact where
    /// a full bar is the truth. Zero or less is a window reported before anything was spent.</remarks>
    public static double? PercentUsed(TokenUsage? usage)
    {
        if (usage?.UsedTokens is not { } used || usage.ContextWindow is not { } window) return null;
        // A window of zero is a figure nobody can divide by and a claim nobody made: the field is there
        // and carries nothing, which is the same answer as not being there at all.
        if (window <= 0) return null;
        return Math.Clamp(used * 100d / window, 0, 100);
    }
}
