namespace mTiles.Views;

/// <summary>What one control on a row costs in width, drawn both ways.</summary>
/// <param name="Full">Drawn in full. Zero for a control that is not shown.</param>
/// <param name="Compact">Drawn compact - a picker as its icon, a status as its dot. The same as
/// <paramref name="Full"/> for a control that has no compact form.</param>
public readonly record struct RowItemWidths(double Full, double Compact)
{
    public static RowItemWidths Hidden => new(0, 0);
}

/// <summary>One step of a row's retreat when it runs out of width.</summary>
public abstract record RetreatStep
{
    /// <summary>These controls go compact, together.</summary>
    public sealed record Compact(params int[] Items) : RetreatStep;

    /// <summary>This control keeps its words but is capped to what the row has left, so they trim - or,
    /// when not even <see cref="RowRetreat.MinTrimmedText"/> of them would fit, the step is passed over.</summary>
    public sealed record Trim(int Item) : RetreatStep;
}

/// <summary>How a row is drawn in the room it has: which controls are compact, and the cap on any that
/// trims (<c>null</c> leaves the markup's own cap alone).</summary>
public sealed record RowShape(bool[] Compact, double?[] MaxWidth)
{
    public static RowShape FullFor(int count) => new(new bool[count], new double?[count]);
}

/// <summary>
/// Which controls on a one-line row give up their words, in which order, as the row narrows.
/// </summary>
/// <remarks>
/// <para><b>Why a row retreats rather than wraps.</b> A <c>WrapPanel</c> measures its children with
/// infinite width, so no trimming inside it ever fires, and a narrow tile gets a second line holding one
/// word — the composer's permission, the strip's status. One line whose parts give way in an order
/// somebody chose keeps the shape and loses the least.</para>
/// <para><b>The order is the caller's, and it is an opinion</b> — which is why each row's steps are
/// written down beside it (<see cref="ComposerPickerLayout"/>, <see cref="AgentStripLayout"/>) and argued
/// in a table test. What is shared is the arithmetic: take the steps in turn, stop at the first that fits,
/// and if none does, the row is as compact as the steps can make it.</para>
/// <para>Pure, and in <c>Views/</c> for the reason <see cref="UsageLayout"/> is. The measuring is
/// <see cref="RowFitter"/>'s.</para>
/// </remarks>
public static class RowRetreat
{
    /// <summary>The least of a name worth showing: below this a trimmed id is an ellipsis and two letters,
    /// which says less than the icon and costs more.</summary>
    public const double MinTrimmedText = 64;

    /// <param name="available">The row's width. Zero or less is a row not yet measured, which is drawn in
    /// full rather than starting compact and springing open on the first layout pass.</param>
    public static RowShape For(double available, IReadOnlyList<RowItemWidths> items, IReadOnlyList<RetreatStep> steps)
    {
        var shape = RowShape.FullFor(items.Count);
        if (available <= 0) return shape;

        double Need()
        {
            var sum = 0.0;
            for (var i = 0; i < items.Count; i++) sum += shape.Compact[i] ? items[i].Compact : items[i].Full;
            return sum;
        }

        if (Need() <= available) return shape;

        foreach (var step in steps)
        {
            switch (step)
            {
                case RetreatStep.Compact compact:
                    foreach (var i in compact.Items) shape.Compact[i] = true;
                    if (Need() <= available) return shape;
                    break;

                case RetreatStep.Trim trim:
                    var room = available - (Need() - items[trim.Item].Full);
                    if (room >= items[trim.Item].Compact + MinTrimmedText)
                    {
                        shape.MaxWidth[trim.Item] = room;
                        return shape;
                    }
                    break;
            }
        }

        return shape;
    }
}
