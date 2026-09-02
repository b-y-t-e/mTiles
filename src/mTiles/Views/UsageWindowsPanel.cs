using Avalonia;
using Avalonia.Controls;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// One account's limit windows, laid out the three ways they are worth laying out.
/// </summary>
/// <remarks>
/// <para><b>Wide, they share one line in equal shares</b> — which is what makes two accounts with the
/// same windows line their figures up down the tile, and it is why this is not a
/// <see cref="WrapPanel"/> with a special case: equal shares are a column, and a wrap packs to the
/// left.</para>
/// <para><b>Narrow, a window with a bar takes a line of its own and one without wraps.</b> The bar is
/// the whole reason a window needs the width — it is the only part of the row with nothing in it, so it
/// is what a shared line starves first. A window answered in money has no bar at all: <c>today: $0.38</c>
/// is eighty pixels of text, and giving each of four of them its own line spent four lines saying what
/// fits in two.</para>
/// <para><b>It asks the item, not the container.</b> A child here is the item's presenter, whose own
/// alignment says nothing about what the template inside it drew, and both shapes measure to much the
/// same width when asked with no constraint — so the discriminator is the one that is actually true:
/// whether that window has a bar to draw.</para>
/// </remarks>
public sealed class UsageWindowsPanel : Panel
{
    /// <summary>Whether the windows are stacked rather than sharing one line.</summary>
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<UsageWindowsPanel, bool>(nameof(IsVertical));

    static UsageWindowsPanel() => AffectsMeasure<UsageWindowsPanel>(IsVerticalProperty);

    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    /// <summary>The gap between two windows on the same line, which is what makes them two.</summary>
    private const double Spacing = 10;

    /// <summary>How tall one window's row is — the readout's own line height.</summary>
    /// <remarks>Taken from what the children ask for rather than set here, with this as the floor so a
    /// row of pure text and a row carrying a bar come out the same height.</remarks>
    private const double MinRowHeight = 15;

    protected override Size MeasureOverride(Size available)
    {
        var children = Children;
        if (children.Count == 0) return default;

        if (!IsVertical)
        {
            var share = available.Width / children.Count;
            var tall = MinRowHeight;
            foreach (var child in children)
            {
                child.Measure(new Size(Math.Max(0, share - Spacing), available.Height));
                tall = Math.Max(tall, child.DesiredSize.Height);
            }

            return new Size(available.Width, tall);
        }

        var width = available.Width;
        var used = 0d;
        var rowHeight = MinRowHeight;
        var height = 0d;

        foreach (var child in children)
        {
            child.Measure(new Size(width, available.Height));

            var whole = TakesWholeLine(child);
            var desired = whole ? width : child.DesiredSize.Width;

            if (used > 0 && used + desired > width)
            {
                height += rowHeight;
                used = 0;
                rowHeight = MinRowHeight;
            }

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            // A window with a bar keeps its line to itself: the next one starts below it however much
            // room is left beside it, or a bar and a figure end up sharing a line with the row that was
            // meant to be under them.
            if (whole)
            {
                height += rowHeight;
                used = 0;
                rowHeight = MinRowHeight;
            }
            else used += desired + Spacing;
        }

        if (used > 0) height += rowHeight;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children;
        if (children.Count == 0) return final;

        if (!IsVertical)
        {
            var share = final.Width / children.Count;
            for (var i = 0; i < children.Count; i++)
                children[i].Arrange(new Rect(i * share, 0,
                    Math.Max(0, share - Spacing), final.Height));

            return final;
        }

        var x = 0d;
        var y = 0d;
        var rowHeight = MinRowHeight;

        foreach (var child in children)
        {
            var whole = TakesWholeLine(child);
            var width = whole ? final.Width : child.DesiredSize.Width;

            if (x > 0 && x + width > final.Width)
            {
                y += rowHeight;
                x = 0;
                rowHeight = MinRowHeight;
            }

            rowHeight = Math.Max(rowHeight, Math.Max(MinRowHeight, child.DesiredSize.Height));
            child.Arrange(new Rect(x, y, width, rowHeight));

            if (whole)
            {
                y += rowHeight;
                x = 0;
                rowHeight = MinRowHeight;
            }
            else x += width + Spacing;
        }

        return final;
    }

    /// <summary>Whether this window is one that wants the whole of a line to itself.</summary>
    private static bool TakesWholeLine(Control child) =>
        child.DataContext is UsageWindowViewModel { HasPercent: true };
}
