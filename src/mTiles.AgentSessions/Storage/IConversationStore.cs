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

    /// <summary>Every conversation something was said in, in a directory, most recently used first.</summary>
    /// <remarks>
    /// <para>What the picker in an Agent tile is built from, and the reason a conversation outliving its tile
    /// is worth keeping rather than an orphan nothing can reach.</para>
    /// <para><b>Scoped to the directory</b> because that is what makes the list somebody's work on this
    /// project rather than everything they have ever asked an agent anywhere.</para>
    /// <para><b>A conversation nobody said anything in is not listed.</b> A row exists from the moment a
    /// session starts, so every tile opened and left, and every new conversation started and abandoned, has
    /// one — and its <c>updated_at</c> never moves, which would sort it to the <i>top</i>. Offered, they are
    /// indistinguishable from each other, name nothing, and can only be removed one at a time by opening one.
    /// The store keeps them; this question does not ask about them.</para>
    /// </remarks>
    IReadOnlyList<ConversationSummary> List(string workingDirectory);

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
/// <param name="Id">What the conversation is called. A tile's own id to begin with — which is what every
/// conversation was before one could be chosen from a list — and any id the tile has since been pointed at.</param>
/// <param name="AgentId">Which agent it was held with; a token is only ever handed back to that agent.</param>
/// <param name="ResumeToken">The agent's handle on the conversation, as last reported.</param>
public sealed record ConversationRecord(
    string Id,
    string AgentId,
    string WorkingDirectory,
    string? ResumeToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Enough of a stored conversation to offer it in a list without replaying it.</summary>
/// <remarks>
/// <para><b>Nothing derived is stored for this</b>, which is the same rule the events themselves follow: the
/// opening is read back out of the first <c>UserMessageAdded</c> at list time rather than kept in a column
/// that a change to how a title reads would leave stale. It is cheap because the events table is keyed by
/// <c>(conversation_id, sequence)</c>, so finding a conversation's first message walks its own rows in order
/// and stops at the first one <i>of that type</i>.</para>
/// <para><see cref="Opening"/> is the user's own words. <see cref="IConversationStore.List"/> never answers
/// <c>null</c> for it — a conversation nobody spoke in is not listed — but a viewer naming a conversation it
/// is showing before anything has been said in it has nothing to put there, so <see cref="ConversationTitle"/>
/// answers for that case rather than each viewer inventing a word.</para>
/// </remarks>
public sealed record ConversationSummary(
    string Id,
    string AgentId,
    DateTimeOffset UpdatedAt,
    string? Opening);
