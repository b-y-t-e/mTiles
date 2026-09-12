using Avalonia.Controls;
using Avalonia.Media;

namespace mTiles.Views;

/// <summary>The one definition of what a drop hint is painted with.</summary>
/// <remarks>Shared by the tile's own hint and the workspace's gutter and edge bands, because two copies
/// of the same accent and weights drift, and a gutter hint in one shade beside a tile hint in another
/// reads as two unrelated gestures.</remarks>
internal static class DropHintBrushes
{
    private const byte FillAlpha = 55;
    private const byte OutlineAlpha = 140;

    /// <summary>The fill and the outline, derived from the theme's <c>AccentHover</c>.</summary>
    /// <remarks>A theme that has not been applied yet answers with transparent brushes: no hint is
    /// better than a colour this application did not derive.</remarks>
    public static (IBrush Fill, IBrush Outline) For(Control host)
    {
        var accent = (host.FindResource("AccentHover") as ISolidColorBrush)?.Color ?? Colors.Transparent;
        return (WithAlpha(accent, FillAlpha), WithAlpha(accent, OutlineAlpha));
    }

    private static SolidColorBrush WithAlpha(Color color, byte alpha) =>
        new(Color.FromArgb(alpha, color.R, color.G, color.B));
}
