namespace mTiles.Views;

/// <summary>
/// The order in which the Goal tile's top strip gives up its words.
/// </summary>
/// <remarks>
/// <para><b>Mode and effort go first, and together</b> — the composer's rule for the same two
/// pickers: their icons say which is which, and their values are one short word each.</para>
/// <para><b>The agent trims, and goes to its icon last</b>, because which agent is writing into this
/// repository is the one value on the strip that has to be readable at a glance.</para>
/// <para>The status is not on this row any more: it moved to the bar under the composer, the place the
/// Agent tile keeps what its conversation is doing — see <see cref="GoalStatusBarLayout"/>.</para>
/// <para>The arithmetic is <see cref="RowRetreat"/>'s.</para>
/// </remarks>
public static class GoalStripLayout
{
    public const int Agent = 0, Mode = 1, Effort = 2;

    public static readonly IReadOnlyList<RetreatStep> Steps =
    [
        new RetreatStep.Compact(Mode, Effort),
        new RetreatStep.Trim(Agent),
        new RetreatStep.Compact(Agent),
    ];
}

/// <summary>
/// The order in which the bar under the Goal tile's composer gives up its words.
/// </summary>
/// <remarks>
/// <para><b>The status trims first, and only then goes to a dot</b>. While a run is going its word is
/// the phase label — which stage, which attempt — and that is the one thing on the bar nothing else on
/// the tile says, so a long label keeps as much of itself as the row has room for rather than
/// collapsing to its colour the moment it is longer than the room.</para>
/// <para>The finding badges are counts nobody wants trimmed and never give way; the activity text is
/// not on the row at all — it fills whatever is left and is the first thing to go.</para>
/// </remarks>
public static class GoalStatusBarLayout
{
    public const int Status = 0, Badges = 1;

    public static readonly IReadOnlyList<RetreatStep> Steps =
    [
        new RetreatStep.Trim(Status),
        new RetreatStep.Compact(Status),
    ];
}
