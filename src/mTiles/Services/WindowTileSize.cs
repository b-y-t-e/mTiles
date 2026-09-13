using Avalonia;
using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// How much room a tile of the window's own layout — a note, a todo list, a usage dashboard — is given
/// when it is put beside something.
/// </summary>
/// <remarks>
/// <para><b>Narrow, because of what these tiles are.</b> They sit beside the workspaces, not in place of
/// them: a list to glance at, a figure to check. Half of whatever they were dropped on — the share a tile
/// inside a workspace gets — took half the window away from the thing the window is for.</para>
/// <para><b>Pixels on a large window, a share on a small one.</b> A panel of fixed width is right while
/// the window has room for it twice over: it stays the size it was given when the window is resized, and
/// the workspace takes the difference. On a window narrower than that, the same pixels would be most of it,
/// so the tile takes a share instead and scales with the window.</para>
/// <para>Pure, and argued in a table test, like every other opinion here about a layout.</para>
/// </remarks>
public static class WindowTileSize
{
    /// <summary>How wide a window tile stands beside the layout.</summary>
    public const double Width = 320;

    /// <summary>How tall a window tile is when it lies along the top or the bottom.</summary>
    public const double Height = 220;

    /// <summary>The share it takes where the window is too small for its pixels.</summary>
    public const double Share = 0.3;

    /// <param name="orientation">The split the tile is put into: <see cref="Orientation.Vertical"/> puts it
    /// beside, so it is the window's width that counts.</param>
    /// <param name="window">The size the window's layout has. Nothing known yet reads as too small, which
    /// gives a share — the answer that cannot take the whole window.</param>
    internal static TileDropSize For(Orientation orientation, Size window)
    {
        var (pixels, along) = orientation == Orientation.Vertical
            ? (Width, window.Width)
            : (Height, window.Height);

        return along > 2 * pixels ? TileDropSize.InPixels(pixels) : TileDropSize.AsShare(Share);
    }
}
