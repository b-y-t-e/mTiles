using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// A control standing for one tile of a tree, which a dragged tile can be dropped onto.
/// </summary>
/// <remarks>
/// <para>What <see cref="TileDropSurface"/> looks for under the pointer, instead of one view type.
/// <c>LeafTileView</c> is the only one today; the tiles the window lays out are drawn by views of their
/// own, and a surface that tested for <c>LeafTileView</c> would have to learn each of them by name.</para>
/// <para>The drawing stays with the target because the hint belongs inside that control's own shape —
/// a tile's card clips it to its radius, and nothing above the card knows that radius.</para>
/// </remarks>
internal interface ITileDropTarget
{
    /// <summary>The tile this control stands for, or null while it has none.</summary>
    LeafTileNodeViewModel? DropNode { get; }

    /// <summary>Draws the hint for a drop landing in <paramref name="zone"/> of this tile.</summary>
    void ShowDropOverlay(DropZone zone);

    /// <summary>Puts the hint away.</summary>
    void HideDropOverlay();
}
