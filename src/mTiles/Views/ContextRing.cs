using Avalonia.Data.Converters;

namespace mTiles.Views;

/// <summary>
/// How far round the composer's context ring is drawn for a share of the window used.
/// </summary>
/// <remarks>
/// The ring replaced an 11px <c>ArrowCollapseVertical</c>, which at that size was two hairlines and a dot
/// nobody could name. Drawn rather than lettered, for the reason <c>Arc.busy-arc</c> is: a glyph fills a
/// path and has no stroke to thicken. Any use at all shows a sliver — the <see cref="UsageBar"/>'s rule
/// that the first cell lights — because an empty ring beside "1%" reads as a figure the drawing disputes.
/// </remarks>
public static class ContextRing
{
    private const double Sliver = 14;

    public static double SweepFor(double? percent) => percent switch
    {
        null or <= 0 => 0,
        >= 100 => 360,
        { } p => Math.Max(Sliver, p * 3.6),
    };

    public static readonly FuncValueConverter<double?, double> Sweep = new(SweepFor);
}
