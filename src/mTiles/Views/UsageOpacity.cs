using System.Globalization;
using Avalonia.Data.Converters;

namespace mTiles.Views;

/// <summary>
/// How strongly something is drawn, from whether it is worth full strength.
/// </summary>
/// <remarks>
/// <para>A reading older than the window it describes is dimmed — it is still the figure, it is just
/// not current.</para>
/// <para>In <c>Views/</c> for the reason <see cref="TileIcons"/> is: how strongly a thing is drawn is a
/// fact about the drawing, and the view model says only whether it is stale.</para>
/// </remarks>
public sealed class UsageOpacity(double whenTrue, double whenFalse) : IValueConverter
{
    /// <summary>Dims what is true — a stale reading.</summary>
    public static readonly UsageOpacity Instance = new(0.55, 1);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? whenTrue : whenFalse;

    /// <summary>One way only: an opacity says nothing about which fact produced it.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
