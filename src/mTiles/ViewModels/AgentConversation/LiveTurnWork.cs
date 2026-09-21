using mTiles.AgentSessions.Conversation;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// Which group of work is the work of the turn that is still going.
/// </summary>
/// <remarks>
/// <para><b>The turn, never the last group.</b> A group is opened at a turn's <i>first</i> tool
/// (<c>ConversationReducer.CurrentGroup</c>), so between the user's message and that first tool the
/// last group in the timeline is the <i>previous</i> turn's — taken as live it springs open again
/// under the reader and folds once the new group appears, and a turn that runs no tools at all leaves
/// somebody else's work open for the whole of it. The turn is on both sides of the question already:
/// the entry carries the turn it was made in and the conversation names the one that is running.</para>
/// <para>Nothing is live while no turn is running, and an entry that carries no turn at all belongs to
/// no running one — a conversation replayed out of the store is folded, which is the safe way round.</para>
/// </remarks>
public static class LiveTurnWork
{
    /// <summary>Whether this group is the work of <paramref name="state"/>'s running turn.</summary>
    public static bool IsLive(WorkGroupEntry? group, ConversationState state) =>
        state.ActiveTurnId is { } running && group?.TurnId == running;
}
