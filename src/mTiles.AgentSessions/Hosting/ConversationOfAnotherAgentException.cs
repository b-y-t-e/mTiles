namespace mTiles.AgentSessions.Hosting;

/// <summary>A conversation was opened for one agent while the store holds it for another.</summary>
public sealed class ConversationOfAnotherAgentException(string conversationId, string storedAgentId)
    : InvalidOperationException($"Conversation {conversationId} belongs to the agent '{storedAgentId}'.")
{
    public string StoredAgentId { get; } = storedAgentId;
}
