namespace mTiles.ViewModels;

/// <summary>
/// Tile content that is worth giving the whole workspace to.
/// </summary>
/// <remarks>
/// <para>The one capability here with no members, and the emptiness is the answer rather than an
/// omission: filling the workspace is done to a <see cref="LeafTileNodeViewModel"/> by
/// <see cref="TileMaximizeScope"/>, which is layout and none of the content's business. What only the
/// content can say is whether the gesture means anything for it, and that is a yes or a no — a
/// <c>bool CanMaximize</c> on <see cref="ITile"/> would be the same answer written where every kind has
/// to repeat it, including the four that would all write <c>false</c>.</para>
/// <para>Who implements it is a decision about what the extra room buys. A terminal, an agent, a note
/// and a todo list are the four whose content is simply <em>more of the same</em> at a larger size —
/// more scrollback, more text, more rows. The git tile, the database tile, the goal tile and the usage
/// tile lay themselves out in panes and columns that are already sized to their own content: making
/// them larger stretches whitespace rather than showing anything more, and the two of them that own a
/// splitter of their own would then have a splitter inside a tile with no splitter around it.</para>
/// <para>It varies while the tile is alive for the reason every other capability here does: an empty
/// tile becomes a terminal, and a leaf that answered no a moment ago answers yes.</para>
/// </remarks>
public interface IMaximizableTile : ITile
{
}
