using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>How a tile draws the hint for a drop landing on it: the zone the tile will be split at, or
/// an outline for a swap.</summary>
/// <remarks>Shared by every control that is an <see cref="ITileDropTarget"/> — a tile's card and the
/// frames the window draws round its list and its workspace — so the hint on one looks like the hint on
/// the other. The overlay is each control's own, laid inside its own clip.</remarks>
internal static class TileDropOverlay
{
    /// <summary>How much of the tile the band for an edge drop leaves uncovered.</summary>
    private const double Uncovered = 0.70;

    public static void Show(Border overlay, DropZone zone, Size bounds, Control brushHost, string brushKey)
    {
        if (zone == DropZone.None)
        {
            Hide(overlay);
            return;
        }

        var (fill, outline) = DropHintBrushes.For(brushHost, brushKey);

        if (zone == DropZone.Center)
        {
            overlay.Background = Brushes.Transparent;
            overlay.BorderBrush = outline;
            overlay.BorderThickness = new Thickness(3);
            overlay.Margin = new Thickness(3);
        }
        else
        {
            var (w, h) = (bounds.Width, bounds.Height);
            overlay.Background = fill;
            overlay.BorderBrush = outline;
            overlay.BorderThickness = new Thickness(2);
            overlay.Margin = zone switch
            {
                DropZone.Left => new Thickness(2, 2, w * Uncovered, 2),
                DropZone.Right => new Thickness(w * Uncovered, 2, 2, 2),
                DropZone.Top => new Thickness(2, 2, 2, h * Uncovered),
                DropZone.Bottom => new Thickness(2, h * Uncovered, 2, 2),
                _ => default
            };
        }

        overlay.IsVisible = true;
    }

    public static void Hide(Border overlay) => overlay.IsVisible = false;
}
