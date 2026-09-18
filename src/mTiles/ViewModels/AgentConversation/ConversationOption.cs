using mTiles.AgentSessions.Storage;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// One stored conversation, as the tile's list offers it.
/// </summary>
/// <param name="Summary">What the store knows about it without replaying it.</param>
/// <param name="AgentName">The agent that holds it — a conversation is never handed to another CLI.</param>
/// <param name="IsCurrent">Whether it is the one this tile is showing.</param>
/// <param name="IsPickable">Whether picking it now would do anything.</param>
/// <param name="Reason">Why it cannot be picked, or null.</param>
/// <remarks>Refused entries are offered, dimmed and carry their reason, the rule
/// <see cref="AgentInstanceOption"/> keeps and for the same two reasons: a row that vanishes explains
/// nothing, and a disabled item is out of Avalonia's hit test so its tooltip is never drawn.</remarks>
public sealed record ConversationOption(
    ConversationSummary Summary,
    string AgentName,
    bool IsCurrent,
    bool IsPickable,
    string? Reason)
{
    /// <summary>The user's own opening words, cut to a line.</summary>
    public string Title => ConversationTitle.For(Summary);

    /// <summary>When it was last used, and which agent holds it — the two things that tell two rows apart when
    /// both begin with the same request.</summary>
    public string Note => $"{ConversationWhen.For(Summary.UpdatedAt, DateTimeOffset.Now)} · {AgentName}";
}
