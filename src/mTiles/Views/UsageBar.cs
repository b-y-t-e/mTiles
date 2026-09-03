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
/// <para><b>The tick is a cell, and it is drawn last.</b> It began as a track cell recoloured — one of
/// the grey ones, named by the clock — which read perfectly wherever it was, and vanished the moment
/// the fill reached it: a window spent past its pace covered the very mark that said so. A line in the
/// gutter between cells survived the fill, but paid for it with the read — squeezed into a gap it was
/// indistinguishable from the fill's own edge wherever the two met, which on an account spending on
/// pace is exactly where it lands (measured against a Claude Code Pro card: 37% spent at 36% of the
/// week gone, and the mark read as the boundary it stood on). So it is back to being a cell — the same
/// size as the rest, which is what makes it read as one of them — and it is drawn over whatever the
/// fill has put there: in the track it is the old mark, under the fill it is a hole punched through
/// the colour, and either way it is where the clock is.</para>
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

        // Last, so that it is over whatever it lands on. It is a cell — the same size as the rest —
        // because that is what it was first, and what it still reads best as: one cell of the track
        // named by the clock. Drawn in the pass it vanished the moment the fill reached it, which is
        // the one moment the mark is worth looking for; drawn last it holds its place in the track
        // exactly as before and survives the fill overrunning it.
        if (expected is { } tick && TickBrush is { } tickBrush)
            context.FillRectangle(tickBrush,
                new Rect(tick * (CellWidth + CellGap), 0, CellWidth, height));
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
    /// <remarks>The mark is a cell like the others, so its index is also its place on the bar; it is
    /// clamped inside the bar rather than allowed to sit one past the end, because a cell drawn at
    /// <c>cells</c> would be off it entirely.</remarks>
    internal static int? MarkCell(double? expectedPercent, int cells) =>
        expectedPercent is { } share
            ? Math.Clamp((int)Math.Round(Fraction(share) * cells), 0, cells - 1)
            : null;

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
