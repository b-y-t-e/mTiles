using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>
/// Tile content that can say what it is doing right now.
/// </summary>
/// <remarks>
/// <para>The whole of what a workspace needs from a tile in order to light its row in the panel —
/// deliberately one property, so a tile that has no notion of working (a note, a todo list) implements
/// nothing and the workspace asks nothing of it. Change notification comes from <see cref="ITile"/>,
/// which every tile has anyway.</para>
/// <para><b>A state and not a flag, and it is still one property.</b> The boolean this replaced could
/// say "something is happening in there" and could not say the one thing worth interrupting somebody
/// for — that the tile has stopped on a question and is waiting. <c>IsBusy</c> is not gone, it has
/// moved to where it is consumed: the panel's row derives it, which is also the rule about one writer
/// per property, since the row draws two different things from the same answer.</para>
/// </remarks>
public interface IBusyTile : ITile
{
    TileActivity Activity { get; }
}
