namespace mTiles.Views;

/// <summary>
/// The order in which the Goal tile's top strip gives up its words.
/// </summary>
/// <remarks>
/// <para><b>Mode and effort go first, and together</b> — the composer's rule for the same two
/// pickers: their icons say which is which, and their values are one short word each.</para>
/// <para><b>The status trims next, and only then goes to a dot</b>. While a run is going its word is
/// the phase label — which stage, which attempt — and that is the one thing on the strip nothing else on
/// the tile says, so a long label keeps as much of itself as the row has room for rather than
/// collapsing to its colour the moment it is longer than the room. The dot is what is left when not even
/// <see cref="RowRetreat.MinTrimmedText"/> of it would fit, as on the Agent tile's strip.</para>
/// <para><b>The agent trims, and goes to its icon last</b>, because which agent is writing into this
/// repository is the one value on the strip that has to be readable at a glance. The finding badges are
/// counts nobody wants trimmed and never give way; the activity text is not on the row at all — it fills
/// whatever is left and is the first thing to go.</para>
/// <para>The arithmetic is <see cref="RowRetreat"/>'s.</para>
/// </remarks>
public static class GoalStripLayout
{
    public const int Agent = 0, Mode = 1, Effort = 2, Status = 3, Badges = 4;

    public static readonly IReadOnlyList<RetreatStep> Steps =
    [
        new RetreatStep.Compact(Mode, Effort),
        new RetreatStep.Trim(Status),
        new RetreatStep.Compact(Status),
        new RetreatStep.Trim(Agent),
        new RetreatStep.Compact(Agent),
    ];
}
