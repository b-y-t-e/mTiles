using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;

namespace mTiles.AgentSessions.Hosting;

/// <summary>
/// Moves a conversation onto another agent or another login: writes the seam and hands back the record the
/// next host opens on.
/// </summary>
/// <remarks>
/// <para><b>Between two hosts, on purpose.</b> It is called with no host alive — the outgoing one disposed
/// and drained, the incoming one not yet built — which is what lets it number the event off the store and
/// what makes it work when the outgoing agent has crashed, died or never started. That is not an edge case:
/// an agent that got stuck is the usual reason somebody hands the work to another one, and a handover
/// available only from a healthy session would be missing exactly when it is wanted.</para>
/// <para><b>Clearing the token is the load-bearing half.</b> <see cref="ConversationRecord.ResumeToken"/> is
/// the outgoing CLI's own handle. Left in place it is handed to the arriving agent at its first launch, and
/// the two measured failures are both silent: <c>codex resume &lt;unknown&gt;</c> opens an interactive
/// picker, which in a launch is a wait nobody knows about, and <c>agy --conversation &lt;unknown&gt;</c>
/// warns, starts a <i>new</i> conversation and exits 0 — so the tile cannot tell a resumed session from a
/// lost one. The same clearing happens in <c>ConversationReducer</c> for the state; both, because the record
/// is what a launch reads and the state is what a viewer reads.</para>
/// <para><b>It does not delete and it does not migrate.</b> The events of the stretch that is ending stay
/// exactly as they were: they are the transcript, they are what the seam is drawn from, and they are what
/// <c>ConversationHandover</c> folded the brief out of.</para>
/// </remarks>
public static class HandoverWriter
{
    /// <summary>Writes the seam and answers the record the conversation now runs under.</summary>
    /// <param name="store">Where the conversation lives.</param>
    /// <param name="record">The conversation as it stands, which the caller has just been running.</param>
    /// <param name="from">The account the work was done as, or null where nothing had said.</param>
    /// <param name="to">The account it is being handed to.</param>
    /// <param name="brief">What the arriving agent is about to be told, verbatim.</param>
    /// <param name="time">For tests; the wall clock otherwise.</param>
    public static ConversationRecord Write(IConversationStore store, ConversationRecord record,
        SessionAccount? from, SessionAccount to, string brief, TimeProvider? time = null)
    {
        var at = (time ?? TimeProvider.System).GetUtcNow();
        var seam = new HandoverRecorded(from, to, brief)
        {
            Sequence = store.LastSequence(record.Id) + 1,
            At = at,
        };

        // The record first: an append that lands while the row still names the outgoing agent would leave a
        // conversation whose newest event says it moved and whose row says it did not — and that row is what
        // the next host checks itself against before it will open at all.
        var moved = record with { AgentId = to.AgentId, ResumeToken = null, UpdatedAt = at };
        store.Save(moved);
        try
        {
            store.Append(moved.Id, [seam]);
        }
        catch
        {
            // The row moved and the seam did not, which is the one shape neither side can read back: the
            // record would name the arriving agent over a transcript that never says it moved, and the next
            // start would refuse the conversation with no account of where that came from. Put it back and
            // let the caller say the handover did not happen.
            store.Save(record);
            throw;
        }
        return moved;
    }
}
