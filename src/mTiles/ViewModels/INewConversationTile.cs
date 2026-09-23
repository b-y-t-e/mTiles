namespace mTiles.ViewModels;

/// <summary>
/// A tile whose content can be started over as a new conversation — the header's "start another" button.
/// </summary>
/// <remarks>
/// <para>The terminal agent tile gets the same button by another route — a fresh <c>TileId</c> and a restart,
/// which is a fact about the tile's identity rather than anything its content does — so it does not implement
/// this. The Agent and Goal tiles do: a new conversation there is the content's own act, with its own question,
/// and the header only needs to know that there is one and what it is called.</para>
/// <para><b>It always asks.</b> The implementation puts the question and answers no where there is no dialog
/// to ask in; the header does not ask a second time.</para>
/// </remarks>
public interface INewConversationTile : ITile
{
    /// <summary>What the button's tooltip and the menu entry say — "New conversation", "New goal".</summary>
    string NewConversationLabel { get; }

    /// <summary>Asks, and on yes starts the content over.</summary>
    Task StartNewConversationAsync();
}
