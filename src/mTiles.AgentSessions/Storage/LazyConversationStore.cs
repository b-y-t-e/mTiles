using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Storage;

/// <summary>
/// A store opened on first use rather than when the application starts.
/// </summary>
/// <remarks>
/// A damaged or locked database, or a missing native SQLite, must cost the conversation tiles and nothing
/// else: opened eagerly, its exception stopped the main window being built. A failed open is not
/// remembered, so the next tile to ask tries again.
/// </remarks>
public sealed class LazyConversationStore(Func<IConversationStore> open) : IConversationStore
{
    private readonly Lazy<IConversationStore> _store = new(open, LazyThreadSafetyMode.PublicationOnly);

    public ConversationRecord? Find(string conversationId) => _store.Value.Find(conversationId);

    public void Save(ConversationRecord record) => _store.Value.Save(record);

    public IReadOnlyList<AgentEvent> ReadEvents(string conversationId) => _store.Value.ReadEvents(conversationId);

    public long LastSequence(string conversationId) => _store.Value.LastSequence(conversationId);

    public void Append(string conversationId, IReadOnlyList<AgentEvent> events) =>
        _store.Value.Append(conversationId, events);

    public void Delete(string conversationId) => _store.Value.Delete(conversationId);
}
