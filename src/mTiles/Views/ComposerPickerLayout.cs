namespace mTiles.Views;

/// <summary>
/// The order in which the Agent composer's three pickers give up their words.
/// </summary>
/// <remarks>
/// <para><b>Effort and permission go first, and together.</b> Their values are one short word each and
/// their icons say which is which, with the value in the tooltip; the model's name is the one value on
/// the row nobody can read off a glyph. So the model is next <i>trimmed</i> — a model id's start is the
/// part that names it — and only when not even a stub of it fits does it go to its icon as well.</para>
/// <para>The arithmetic is <see cref="RowRetreat"/>'s.</para>
/// </remarks>
public static class ComposerPickerLayout
{
    public const int Model = 0, Effort = 1, Mode = 2;

    public static readonly IReadOnlyList<RetreatStep> Steps =
    [
        new RetreatStep.Compact(Effort, Mode),
        new RetreatStep.Trim(Model),
        new RetreatStep.Compact(Model),
    ];
}
