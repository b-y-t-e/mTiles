using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Storage;

/// <summary>
/// Where conversations live between launches: a row saying what each one is, and its events in order.
/// </summary>
/// <remarks>
/// <para><b>The events are the record, and nothing derived is kept.</b> What a conversation looks like is
/// recomputed from them by <see cref="Conversation.ConversationReducer"/> every time it is opened, so a
/// change to how the timeline is drawn changes every stored conversation at once, and there is no second
/// copy to disagree with the first — the same construction t3code's event store has.</para>
/// <para>Not the agent's own transcript. The CLI keeps that for its own resume, in its own format; this
/// keeps what the user saw, in ours, which is what a later web view will read.</para>
/// </remarks>
public interface IConversationStore
{
    /// <summary>The conversation, or null when there is none by that id.</summary>
    ConversationRecord? Find(string conversationId);

    /// <summary>Writes the conversation's row, creating it if it is new.</summary>
    void Save(ConversationRecord record);

    /// <summary>Every event of the conversation, oldest first.</summary>
    IReadOnlyList<AgentEvent> ReadEvents(string conversationId);

    /// <summary>The highest sequence number stored for the conversation, 0 when it has none — counting the
    /// events <see cref="ReadEvents"/> skips, so a new event never takes the number of one it could not read.</summary>
    long LastSequence(string conversationId);

    /// <summary>Appends events that already carry their sequence numbers, in one transaction.</summary>
    void Append(string conversationId, IReadOnlyList<AgentEvent> events);

    /// <summary>Removes the conversation and all of its events.</summary>
    void Delete(string conversationId);
}

/// <summary>What a stored conversation is.</summary>
/// <param name="Id">The tile's own id — one conversation per agent tile.</param>
/// <param name="AgentId">Which agent it was held with; a token is only ever handed back to that agent.</param>
/// <param name="ResumeToken">The agent's handle on the conversation, as last reported.</param>
public sealed record ConversationRecord(
    string Id,
    string AgentId,
    string WorkingDirectory,
    string? ResumeToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
