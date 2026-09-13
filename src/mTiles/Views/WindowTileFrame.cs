using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// Draws one of the window's permanent tiles — the list of workspaces, or the place the open workspace is
/// drawn — without a card or a header of its own.
/// </summary>
/// <remarks>
/// <para>Neither of them is a card in the sense a note is. The list already is one, with its own heading;
/// the workspace is a canvas of cards, and a second card round it would be a frame round the frames. So
/// the frame adds nothing to look at and only what the tree needs: it is the tile's drop target, and it
/// lays the drop hint over whatever it holds.</para>
/// <para>What it holds is not its own. The list's view and the panel of cached workspace views belong to
/// the window and are moved into whichever frame currently stands for their tile, so rebuilding the tree
/// — moving the list to the top — re-parents them instead of building them again, and no workspace's
/// shells are ended by it.</para>
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
