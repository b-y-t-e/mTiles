namespace mTiles.Views;

/// <summary>How big a dialog's card is, as the dialog itself asks for it.</summary>
/// <remarks>
/// <para>Two shapes, because dialogs come in two kinds. A question is a fixed width and as tall as
/// what it says — <see cref="Fixed"/>. A page is a share of the window, because its content is a list
/// somebody scrolls and it should grow with the room available: <see cref="Fraction"/>, which is what
/// Settings had worked out for itself in code-behind before <c>OverlayHost</c> drew it.</para>
/// <para>Either way the host still clamps the card to the window it is drawn in, so a dialog written
/// for a wide screen narrows on a small one rather than running off the edge.</para>
/// </remarks>
/// <param name="Width">A fixed width, or null to size to the content.</param>
/// <param name="Height">A fixed height, or null to size to the content.</param>
/// <param name="WidthFraction">A share of the window's width, or 0 for none.</param>
/// <param name="HeightFraction">A share of the window's height, or 0 for none.</param>
/// <param name="MinWidth">The floor a fraction may not go below.</param>
/// <param name="MinHeight">The floor a fraction may not go below.</param>
public readonly record struct OverlaySize(
    double? Width = null,
    double? Height = null,
    double WidthFraction = 0,
    double HeightFraction = 0,
    double MinWidth = 0,
    double MinHeight = 0)
{
    public static OverlaySize Fixed(double width, double? height = null) => new(width, height);

    public static OverlaySize Fraction(double width, double height, double minWidth, double minHeight) =>
        new(WidthFraction: width, HeightFraction: height, MinWidth: minWidth, MinHeight: minHeight);
}
