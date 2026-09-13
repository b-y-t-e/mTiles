using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// Draws the place the open workspace is shown, as a tile of the window's layout, without a card or a
/// header of its own.
/// </summary>
/// <remarks>
/// <para>The workspace is a canvas of cards, and a card round it would be a frame round the frames — so
/// the frame adds nothing to look at and only what the tree needs: it is the tile's drop target, and it
/// lays the drop hint over whatever it holds. The list of workspaces used to be drawn in one of these too;
/// it is an ordinary card now, with the header every tile is dragged and split by.</para>
/// <para>What it holds is not its own. The panel of cached workspace views belongs to the window and is
/// moved into whichever frame currently stands for the tile, so rebuilding the tree re-parents it instead
/// of building it again, and no workspace's shells are ended by it.</para>
/// </remarks>
internal sealed class WindowTileFrame : Grid, ITileDropTarget
{
    private readonly Border _overlay = new()
    {
        IsVisible = false,
        IsHitTestVisible = false,
        ZIndex = 100,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    public WindowTileFrame(LeafTileNodeViewModel tile, Control content)
    {
        DataContext = tile;
        DropNode = tile;

        ControlHelper.DetachFromParent(content);
        Children.Add(content);

        _overlay.Bind(Border.CornerRadiusProperty, _overlay.GetResourceObservable("RadiusTile"));
        Children.Add(_overlay);
    }

    public LeafTileNodeViewModel? DropNode { get; }

    public void ShowDropOverlay(DropZone zone, string brushKey) =>
        TileDropOverlay.Show(_overlay, zone, Bounds.Size, this, brushKey);

    public void HideDropOverlay() => TileDropOverlay.Hide(_overlay);
}
