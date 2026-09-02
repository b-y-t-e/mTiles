using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace mTiles.Views;

/// <summary>
/// One limit window drawn as a bar, with the clock's own share marked on it.
/// </summary>
/// <remarks>
/// <para><b>A bar is a bar and the pace is a tick on it.</b> The tick sits where the elapsed share of
/// the window is, so the one glance that answers <i>how much is left</i> also answers <i>am I
/// overspending</i> — which is what the pace was asked for. Fill past the tick is drawn in the danger
/// colour and fill behind it stays on the accent; that overspend is the only colour on this tile
/// carrying meaning.</para>
/// <para>A control rather than a grid of bound star columns, because a fraction is not a
/// <see cref="GridLength"/> and every route from one to the other is a converter doing arithmetic in
/// the markup. Three rectangles and a line are less code than that, and they land on whole pixels.</para>
/// </remarks>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> UsedPercentProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(UsedPercent));

    /// <summary>Where the clock is, or null when nothing could be worked out — in which case no tick is
    /// drawn, rather than one at zero.</summary>
    public static readonly StyledProperty<double?> ExpectedPercentProperty =
        AvaloniaProperty.Register<UsageBar, double?>(nameof(ExpectedPercent));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> OverBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(OverBrush));

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(TickBrush));

    static UsageBar() =>
        AffectsRender<UsageBar>(UsedPercentProperty, ExpectedPercentProperty, TrackBrushProperty,
            FillBrushProperty, OverBrushProperty, TickBrushProperty);

    public double UsedPercent
    {
        get => GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public double? ExpectedPercent
    {
        get => GetValue(ExpectedPercentProperty);
        set => SetValue(ExpectedPercentProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? OverBrush
    {
        get => GetValue(OverBrushProperty);
        set => SetValue(OverBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var cells = (int)Math.Floor((width + CellGap) / (CellWidth + CellGap));
        if (cells <= 0) return;

        // Anything above nothing lights the first cell, so an account that has spent a little and one
        // that has spent nothing do not draw the same bar - the rounding would otherwise swallow every
        // figure under half a cell, which on a sixteen-cell bar is three per cent.
        var filled = UsedPercent <= 0 ? 0
            : Math.Clamp((int)Math.Round(Fraction(UsedPercent) * cells), 1, cells);

        // The clock's own share as a cell index. It marks an *unfilled* cell: past the fill it is where
        // the spending should have reached by now, and behind it there is nothing to say, because the
        // fill already covers it.
        var expected = ExpectedPercent is { } share
            ? Math.Clamp((int)Math.Round(Fraction(share) * cells), 0, cells - 1)
            : (int?)null;

        for (var cell = 0; cell < cells; cell++)
        {
            var brush =
                cell >= filled ? (cell == expected ? TickBrush ?? TrackBrush : TrackBrush)
                : expected is { } mark && cell >= mark ? OverBrush ?? FillBrush
                : FillBrush;

            if (brush is null) continue;

            context.FillRectangle(brush,
                new Rect(cell * (CellWidth + CellGap), 0, CellWidth, height));
        }
    }

    /// <summary>How wide one cell is drawn.</summary>
    /// <remarks>A segmented bar rather than a continuous fill, and the cells are a fixed size rather
    /// than a fraction of the width: what is being read here is a rough level at a glance, and discrete
    /// cells of a constant size make two bars of different lengths comparable by counting rather than
    /// by measuring.</remarks>
    private const double CellWidth = 4;

    /// <summary>The surface showing between two cells, which is what makes them cells.</summary>
    private const double CellGap = 2;

    private static double Fraction(double percent) => Math.Clamp(percent, 0, 100) / 100;
}
