namespace mTiles.Views;

/// <summary>
/// Whether a transcript that has just grown should follow its own end.
/// </summary>
/// <remarks>
/// <para>The terminal's rule, stated for a scroller: <b>follow only a reader who was already at the
/// bottom</b>. Somebody reading back through a run must not be yanked to the end because a message
/// arrived, and somebody watching a run must not have to chase it.</para>
/// <para>The threshold is what makes that workable rather than exact. A reader sitting at the bottom is
/// not at offset zero from it — the last message is measured after the decision is taken, an inner
/// markdown view settles at its final height a pass later, and a scroller rounds — so a line and a half
/// of slack keeps the follow through all of that while a deliberate scroll up, which is a screen or
/// more, plainly loses it.</para>
/// <para><b>Content that fits is the case worth spelling out</b>, and it falls out of the same
/// subtraction rather than needing a branch: with no scrollbar the extent is no taller than the
/// viewport, so the distance is zero or negative and the answer is yes. A tile nobody has ever
/// scrolled follows, which is what makes the rule invisible until the moment it is wanted.</para>
/// <para>Pure and in a class of its own for the reason <c>ActivityPolicy</c> and <c>TileMinimumSize</c>
/// are: this is an opinion about behaviour, and an opinion is argued in a table test rather than
/// rediscovered from a screenshot. The scroller it applies to is not testable; the rule is.</para>
/// </remarks>
public static class TranscriptFollow
{
    /// <summary>How far off the bottom still counts as watching rather than reading back.</summary>
    public const double StuckToBottom = 48;

    /// <summary>
    /// Whether to scroll to the end, given where the reader was <em>before</em> the new content was
    /// measured.
    /// </summary>
    /// <param name="extent">The whole scrollable height.</param>
    /// <param name="viewport">How much of it is on screen.</param>
    /// <param name="offset">How far down the reader has scrolled.</param>
    /// <remarks>Asked before the layout pass, and that is not an implementation detail: a turn later
    /// the extent has grown by the height of whatever just arrived, and every reader — including the
    /// one who was at the bottom — looks scrolled up by exactly that much.</remarks>
    public static bool ShouldFollow(double extent, double viewport, double offset) =>
        extent - viewport - offset <= StuckToBottom;
}
