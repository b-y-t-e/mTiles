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
/// <para><b>The tick is a line in the gap between two cells, and it is drawn in both states.</b> It
/// began as a differently-coloured <em>cell</em>, which could only be drawn where the fill had not
/// reached — so the moment an account started overspending, which is the one moment the mark is worth
/// looking for, it vanished under the fill and all that was left was the place where the accent turns
/// to the danger colour. That boundary is legible once you know to look for it and invisible until
/// then, which is the opposite of what a marker is for. A line in the gutter needs no cell of its own,
/// so it survives being overtaken: one marker, the same one, whichever side of it the fill has
/// reached.</para>
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
    /// <remarks>Null is <em>there is no pace to state</em>: a window whose length the service did not
    /// name, or one already past its reset. A tick at zero would be a claim that no time has passed.
    /// </remarks>
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

        var cells = CellsIn(width);
        if (cells <= 0) return;

        var filled = FilledCells(UsedPercent, cells);

        // The clock's own share as a cell index: the first cell the spending should not have reached
        // yet. Everything from here on is drawn as overspend, and the tick goes in the gap in front of
        // it.
        var expected = MarkCell(ExpectedPercent, cells);

        for (var cell = 0; cell < cells; cell++)
        {
            var brush =
                cell >= filled ? TrackBrush
                : expected is { } mark && cell >= mark ? OverBrush ?? FillBrush
                : FillBrush;

            if (brush is null) continue;

            context.FillRectangle(brush,
                new Rect(cell * (CellWidth + CellGap), 0, CellWidth, height));
        }

        // Last, so that it is over whatever it lands on: the track behind the fill, the accent, or the
        // danger colour. Its own place is the gutter, which belongs to no cell, so nothing it is drawn
        // across is hidden by it.
        if (expected is { } tick && TickBrush is { } tickBrush)
            context.FillRectangle(tickBrush, new Rect(MarkOffset(tick), 0, CellGap, height));
    }

    /// <summary>How many whole cells this width holds.</summary>
    internal static int CellsIn(double width) =>
        (int)Math.Floor((width + CellGap) / (CellWidth + CellGap));

    /// <summary>How many of them the spending lights.</summary>
    /// <remarks>Anything above nothing lights the first cell, so an account that has spent a little and
    /// one that has spent nothing do not draw the same bar — the rounding would otherwise swallow every
    /// figure under half a cell, which on a sixteen-cell bar is three per cent.</remarks>
    internal static int FilledCells(double usedPercent, int cells) =>
        usedPercent <= 0 ? 0 : Math.Clamp((int)Math.Round(Fraction(usedPercent) * cells), 1, cells);

    /// <summary>Which cell the clock has reached, or null when there is no pace to draw.</summary>
    internal static int? MarkCell(double? expectedPercent, int cells) =>
        expectedPercent is { } share
            ? Math.Clamp((int)Math.Round(Fraction(share) * cells), 0, cells - 1)
            : null;

    /// <summary>Where the tick is drawn: the gap in front of its cell.</summary>
    /// <remarks>A mark at the first cell has no gap in front of it, so it sits at the bar's own edge
    /// rather than off it — the one place the line covers pixels a cell would otherwise have, and the
    /// state (a window that has just reset) where there is nothing there to cover.</remarks>
    internal static double MarkOffset(int cell) =>
        Math.Max(0, cell * (CellWidth + CellGap) - CellGap);

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
