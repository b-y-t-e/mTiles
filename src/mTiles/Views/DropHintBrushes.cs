using Avalonia.Controls;
using Avalonia.Media;

namespace mTiles.Views;

/// <summary>The one definition of what a drop hint is painted with.</summary>
/// <remarks>
/// <para>Shared by a tile's own hint and a surface's gutter and edge bands, because two copies of the
/// same accent and weights drift, and a gutter hint in one shade beside a tile hint in another reads as
/// two unrelated gestures.</para>
/// <para><b>One colour per level of tiles, and only that.</b> The window's layout and a workspace's are
/// drawn one inside the other, so the same gesture over the same part of the screen can mean "put this
/// beside the tiles in here" or "put this beside the whole workspace". The level is the one thing the
/// hint cannot show by its shape — both are the same bands — so it is what the colour says: the
/// workspace's hints keep the accent they have always had, and the window's are the theme's magenta.
/// The weights stay the same for both, so the two are still visibly one gesture.</para>
/// </remarks>
internal static class DropHintBrushes
{
    /// <summary>The colour a workspace's drop hints are painted in.</summary>
    public const string WorkspaceKey = "DropHintWorkspace";

    /// <summary>The colour the window's drop hints are painted in.</summary>
    public const string WindowKey = "DropHintWindow";

    private const byte FillAlpha = 55;
    private const byte OutlineAlpha = 140;

    /// <summary>The fill and the outline, derived from the colour resource <paramref name="key"/>.</summary>
    /// <remarks>A theme that has not been applied yet answers with transparent brushes: no hint is
    /// better than a colour this application did not derive.</remarks>
    public static (IBrush Fill, IBrush Outline) For(Control host, string key)
    {
        var colour = (host.FindResource(key) as ISolidColorBrush)?.Color ?? Colors.Transparent;
        return (WithAlpha(colour, FillAlpha), WithAlpha(colour, OutlineAlpha));
    }

    private static SolidColorBrush WithAlpha(Color color, byte alpha) =>
        new(Color.FromArgb(alpha, color.R, color.G, color.B));
}
