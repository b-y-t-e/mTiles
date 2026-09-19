using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.Controls;

namespace mTiles.Views;

/// <summary>
/// Keeps a one-line row fitted to its width: measures each control drawn in full and drawn compact, asks
/// <see cref="RowRetreat"/> which give way, and applies the answer as the <c>compact</c> class and a cap.
/// </summary>
/// <remarks>
/// <para>A control's compact form is whatever its own styles draw for <c>.compact</c> — a picker as its
/// icon (<c>Picker.compact</c> in mTiles.Controls), a status as its dot. A control with no such style
/// measures the same both ways and simply never gives way.</para>
/// <para><b>It is asked again</b> when the row is resized and when anything that moves a control's own
/// width changes: a picker's value, a control appearing or going, the font (the strip and the composer
/// take theirs from <c>TermFontSm</c>, so Terminal Font Size and the desktop's text scale change every
/// width while the row stays as it was), and whatever else the owner names through <see cref="Watch"/>.
/// Posted rather than run in place, so a burst of changes is one pass and nothing re-measures in the
/// middle of the layout pass that raised it.</para>
/// </remarks>
internal sealed class RowFitter
{
    private const string CompactClass = "compact";

    private readonly Control _row;
    private readonly Control[] _items;
    private readonly double[] _caps;
    private readonly IReadOnlyList<RetreatStep> _steps;
    private bool _queued;
    private bool _deferred;

    public RowFitter(Control row, IReadOnlyList<RetreatStep> steps, params Control[] items)
    {
        _row = row;
        _steps = steps;
        _items = items;
        _caps = items.Select(item => item.MaxWidth).ToArray();

        row.SizeChanged += (_, _) => Queue();
        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (ChangesWhatAControlNeeds(e.Property)) Queue();
            };
        }
    }

    /// <summary>Refits when this property of that object changes — for a width that moves with something
    /// inside a control rather than on it, such as the status's text.</summary>
    public RowFitter Watch(AvaloniaObject source, AvaloniaProperty property)
    {
        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == property) Queue();
        };
        return this;
    }

    private static bool ChangesWhatAControlNeeds(AvaloniaProperty property) =>
        property == Picker.TriggerTextProperty
        || property == Visual.IsVisibleProperty
        || property == TextElement.FontSizeProperty
        || property == TextElement.FontFamilyProperty;

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Fit();
        }, DispatcherPriority.Loaded);
    }

    private void Fit()
    {
        if (!CanBeDrawn())
        {
            DeferUntilDrawable();
            return;
        }

        for (var i = 0; i < _items.Length; i++) _items[i].MaxWidth = _caps[i];

        var shape = RowRetreat.For(_row.Bounds.Width, _items.Select(WidthsOf).ToArray(), _steps);

        for (var i = 0; i < _items.Length; i++)
        {
            _items[i].Classes.Set(CompactClass, shape.Compact[i]);
            // The widths the rule works in are desired sizes, which carry a control's margin, while
            // MaxWidth does not - a cap handed straight over would leave the row that margin too wide.
            if (shape.MaxWidth[i] is { } max)
                _items[i].MaxWidth = Math.Min(_caps[i], max - _items[i].Margin.Left - _items[i].Margin.Right);
        }
    }

    private bool CanBeDrawn() =>
        _row.IsAttachedToVisualTree() && _row.IsEffectivelyVisible && _row.Bounds.Width > 0;

    /// <summary>Holds a fit asked for while the row could not be drawn until the first layout pass in
    /// which it can.</summary>
    /// <remarks>A row in a hidden workspace or a tile being re-parented would be measured against nothing,
    /// so the fit waits instead. It cannot wait for <c>SizeChanged</c>: a workspace is hidden by an
    /// ancestor's <c>IsVisible</c>, which leaves the row's bounds as they were, so showing it again at the
    /// same window size raises nothing on the row - and a picker's value or the font changed meanwhile
    /// would stay fitted to the old text until something else asked.</remarks>
    private void DeferUntilDrawable()
    {
        if (_deferred) return;
        _deferred = true;
        _row.LayoutUpdated += FitOnceDrawable;
    }

    private void FitOnceDrawable(object? sender, EventArgs e)
    {
        if (!CanBeDrawn()) return;
        _deferred = false;
        _row.LayoutUpdated -= FitOnceDrawable;
        Queue();
    }

    /// <summary>What a control needs drawn in full and drawn compact, measured by drawing it both ways.
    /// The class it ends up with is set by <see cref="Fit"/> straight after.</summary>
    private static RowItemWidths WidthsOf(Control item)
    {
        if (!item.IsVisible) return RowItemWidths.Hidden;

        item.Classes.Set(CompactClass, false);
        var full = MeasureAfresh(item);
        item.Classes.Set(CompactClass, true);
        return new RowItemWidths(full, MeasureAfresh(item));
    }

    /// <summary>Measures a control as it is drawn now, not as it was last measured.</summary>
    /// <remarks><b>This is what stopped the composer's row flickering.</b> <c>Measure</c> skips a control
    /// whose last measure is still valid for the same constraint, and toggling <c>compact</c> invalidates
    /// only what the class reaches - the text and the chevron deep in a template - never the control or
    /// what stands between them. So the second of two measures often answered with the first one's size,
    /// the rule got a different pair of widths each pass, and on a row near the threshold every pass
    /// flipped the answer, which changed the row's height, which asked again.</remarks>
    private static double MeasureAfresh(Control item)
    {
        foreach (var layoutable in item.GetVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
        item.InvalidateMeasure();
        item.Measure(Size.Infinity);
        return item.DesiredSize.Width;
    }
}
