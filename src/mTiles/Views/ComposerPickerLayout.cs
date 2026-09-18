namespace mTiles.Views;

/// <summary>What one picker on the composer's settings row costs in width, drawn both ways.</summary>
/// <param name="Full">With its icon, its value and its chevron. Zero for a picker that is not shown.</param>
/// <param name="Compact">With its icon alone.</param>
public readonly record struct PickerWidths(double Full, double Compact)
{
    public static PickerWidths Hidden => new(0, 0);
}

/// <summary>How the composer's three pickers are drawn in the room the row has.</summary>
/// <param name="ModelMaxWidth">The most the model's trigger may take, so its name trims rather than
/// pushing the other two off the row. <c>null</c> leaves the markup's own cap alone.</param>
public readonly record struct ComposerPickerShape(
    bool ModelCompact, bool EffortCompact, bool ModeCompact, double? ModelMaxWidth)
{
    public static ComposerPickerShape Full => new(false, false, false, null);
}

/// <summary>
/// Which of the composer's pickers give up their words when the row is too narrow for all three.
/// </summary>
/// <remarks>
/// <para><b>The row used to wrap.</b> The pickers sat in a <c>WrapPanel</c>, which measures its children
/// with infinite width, so the trigger's own trimming never fired and a narrow tile put the permission on
/// a second line under the model — a composer two rows of settings tall for three words.</para>
/// <para><b>Effort and permission go first, and together.</b> Their values are one short word each and
/// their icons say which is which, with the value in the tooltip; the model's name is the one value on
/// the row nobody can read off a glyph. So the model is next <i>trimmed</i> — a model id's start is the
/// part that names it — and only when not even a stub of it fits does it go to its icon as well.</para>
/// <para>Pure, and in <c>Views/</c> for the reason <see cref="UsageLayout"/> is: an opinion about the
/// drawing, argued in a table test rather than in a code-behind nothing can reach.</para>
/// </remarks>
public static class ComposerPickerLayout
{
    /// <summary>The least of a model's name worth showing: below this a trimmed id is an ellipsis and
    /// two letters, which says less than the icon and costs more.</summary>
    public const double MinModelText = 64;

    /// <param name="available">The row's width. Zero or less is a row not yet measured, which is drawn
    /// in full rather than starting compact and springing open on the first layout pass.</param>
    public static ComposerPickerShape For(double available, PickerWidths model, PickerWidths effort, PickerWidths mode)
    {
        if (available <= 0) return ComposerPickerShape.Full;
        if (model.Full + effort.Full + mode.Full <= available) return ComposerPickerShape.Full;

        var forModel = available - effort.Compact - mode.Compact;
        if (model.Full <= forModel) return new(false, true, true, null);
        if (forModel >= model.Compact + MinModelText) return new(false, true, true, forModel);
        return new(true, true, true, null);
    }
}
