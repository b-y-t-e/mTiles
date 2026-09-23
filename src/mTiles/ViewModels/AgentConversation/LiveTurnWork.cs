using mTiles.AgentSessions.Conversation;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// Which group of work is the work of the turn that is still going.
/// </summary>
/// <remarks>
/// <para>The answer decides what a folded group's one line says and nothing else
/// (<see cref="WorkGroupItemViewModel.Headline"/>): the live turn's group names the tool running right
/// now, every other group tallies what its work came to. Nothing here opens or folds a group.</para>
/// <para><b>The turn, never the last group.</b> A group is opened at a turn's <i>first</i> tool
/// (<c>ConversationReducer.CurrentGroup</c>), so between the user's message and that first tool the
/// last group in the timeline is the <i>previous</i> turn's — taken as live it would report the
/// previous turn's leftover tool as what the agent is doing now, and a turn that runs no tools at all
/// would have it say so for the whole of it. The turn is on both sides of the question already: the
/// entry carries the turn it was made in and the conversation names the one that is running.</para>
/// <para>Nothing is live while no turn is running, and an entry that carries no turn at all belongs to
/// no running one — a conversation replayed out of the store holds whatever state its last draw
/// wrote, so read as live it would claim to be at work for ever.</para>
/// </remarks>
public static class LiveTurnWork
{
    /// <summary>Whether this group is the work of <paramref name="state"/>'s running turn.</summary>
    public static bool IsLive(WorkGroupEntry? group, ConversationState state) =>
        state.ActiveTurnId is { } running && group?.TurnId == running;
}
