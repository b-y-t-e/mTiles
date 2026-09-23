using System.Globalization;
using Avalonia.Data.Converters;

namespace mTiles.Views;

/// <summary>
/// Which side of the transcript a message sits on, and how much of the width it may take.
/// </summary>
/// <remarks>
/// <para>What you typed goes to the right and stops at three quarters of the tile; everything else runs
/// the full width from the left. That is the arrangement every messaging application uses, and the reason
/// is the same one the gutter glyph was there for: in a column of turns, the first thing a reader needs is
/// who is speaking, and position says it before a single character is read.</para>
/// <para><b>A grid of two columns rather than an alignment and a percentage</b>, because Avalonia has no
/// percentage width: the row sits in a <c>1*,3*</c> grid, in the second column when it is yours and
/// spanning both when it is not. Exact, needs nobody's measured width, and cannot be thrown off by which
/// ancestor a binding happens to find.</para>
/// <para>The <c>❯</c> in the gutter goes with it, for your messages only. Two things saying the same
/// thing is one of them being ignored, and the one that costs a column of width is the one to drop.</para>
/// </remarks>
public static class BubbleLayout
{
    /// <summary>The column a row starts in: yours in the second, everything else in the first.</summary>
    public static readonly IValueConverter Column =
        new FuncValueConverter<bool, int>(isUser => isUser ? 1 : 0);

    /// <summary>How many columns it spans: yours one, everything else both.</summary>
    public static readonly IValueConverter Span =
        new FuncValueConverter<bool, int>(isUser => isUser ? 1 : 2);

    /// <summary>The same pair for the Goal tile, whose messages carry a role rather than a flag.</summary>
    /// <remarks>Its own converter instead of comparing the enum in the markup, because a
    /// <c>Classes.</c> trigger can take an <c>ObjectConverters.Equal</c> and an attached property's value
    /// cannot: it wants the number, not the comparison.</remarks>
    public static readonly IValueConverter ColumnForRole = new RoleConverter(userValue: 1, otherValue: 0);

    /// <inheritdoc cref="ColumnForRole"/>
    public static readonly IValueConverter SpanForRole = new RoleConverter(userValue: 1, otherValue: 2);

    private sealed class RoleConverter(int userValue, int otherValue) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Equals(value, parameter) ? userValue : otherValue;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Avalonia.Data.BindingOperations.DoNothing;
    }
}
