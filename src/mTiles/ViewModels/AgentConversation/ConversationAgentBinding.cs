using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// Which agent a conversation belongs to: nobody until something has been said in it, then the agent that held
/// it when it was.
/// </summary>
/// <remarks>
/// <para>t3code's rule and ours for the same reason: the resume token belongs to the CLI that issued it, and the
/// stored events are that agent's. <b>Only a user message binds</b>, on screen as in the store — a notice that a
/// start failed is drawn too and says nothing was, and a host writes a stored record as its session starts,
/// before anything is said.</para>
/// <para>Judged by what is stored as well as by what is drawn: a restored tile is empty until its start has read
/// the store, and an agent picked in that window is not the one the conversation belongs to.</para>
/// </remarks>
public sealed class ConversationAgentBinding(IConversationStore store)
{
    private string? _openedAgentId;
    private bool _hasUserMessage;
    private bool _messageSent;
    private string? _storedAgentOtherThanRunning;

    /// <summary>The agent holding the conversation, or null while nothing has been said.</summary>
    /// <param name="runningAgentId">The tile's agent, for a conversation drawn before any host opened.</param>
    /// <remarks>A stored conversation of another agent outranks the timeline, and a drawn timeline is the agent
    /// whose host drew it, not whichever agent was picked since.</remarks>
    public string? HeldAgentId(string runningAgentId) =>
        _storedAgentOtherThanRunning ?? (_hasUserMessage || _messageSent ? _openedAgentId ?? runningAgentId : null);

    /// <summary>A host of this agent now draws the conversation.</summary>
    public void Opened(string agentId)
    {
        _openedAgentId = agentId;
        _messageSent = false;
    }

    /// <summary>A message was handed to the open host: the conversation is bound from this moment, before the
    /// host has recorded it and long before the screen draws it.</summary>
    public void MessageSent() => _messageSent = true;

    /// <summary>What the conversation on screen says about whether anything has been said.</summary>
    public void Drawn(ConversationState state) =>
        _hasUserMessage = state.Timeline.Any(entry => entry is MessageEntry { Role: MessageRole.User });

    /// <summary>Records which other agent's stored conversation stopped a start, or null once none does, so that
    /// agent is offered back instead of refused against the one that was picked.</summary>
    public void HoldStoredConversationOf(string? agentId) => _storedAgentOtherThanRunning = agentId;

    /// <summary>Whether the stored conversation has a message in it.</summary>
    public Task<bool> HasSomethingSaidAsync(string conversationId) =>
        Task.Run(() => store.ReadEvents(conversationId).Any(e => e is UserMessageAdded));

    /// <summary>The agent of a stored conversation when something has been said in it, else null.</summary>
    public async Task<string?> StoredAgentAsync(string conversationId)
    {
        if (conversationId.Length == 0) return null;
        return await Task.Run(() => store.Find(conversationId)) is { } stored
               && await HasSomethingSaidAsync(conversationId)
            ? stored.AgentId
            : null;
    }
}
