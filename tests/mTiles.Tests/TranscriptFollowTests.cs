using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// When a growing transcript follows its own end, and when it leaves the reader where they are.
/// </summary>
/// <remarks>
/// The terminal's rule, and the reason the Goal tile needs it stated: a run adds a message every few
/// seconds, so a tile that always scrolled would make reading back through a run impossible, and one
/// that never scrolled would have to be chased by hand for the whole of it.
/// </remarks>
public class TranscriptFollowTests
{
    /// <param name="extent">The whole scrollable height.</param>
    /// <param name="viewport">How much of it is on screen.</param>
    /// <param name="offset">How far down the reader has scrolled.</param>
    [Theory]
    // Sitting at the bottom, watching the run.
    [InlineData(2000, 500, 1500, true)]
    // A few pixels off it — the last message being measured, an inner markdown view settling, a
    // scroller rounding. All of that has to keep the follow.
    [InlineData(2000, 500, 1470, true)]
    [InlineData(2000, 500, 1452, true)]
    // A line and a half is the line. Past it, this is somebody reading.
    [InlineData(2000, 500, 1451, false)]
    [InlineData(2000, 500, 1200, false)]
    [InlineData(2000, 500, 0, false)]
    // Everything fits, so there is no scrollbar and nobody has scrolled anywhere: the tile follows,
    // which is what makes the rule invisible until it is wanted. No branch produces this - it is the
    // same subtraction coming out at zero or below.
    [InlineData(300, 500, 0, true)]
    [InlineData(500, 500, 0, true)]
    public void A_reader_at_the_bottom_is_followed_and_a_reader_reading_back_is_not(
        double extent, double viewport, double offset, bool follows) =>
        Assert.Equal(follows, TranscriptFollow.ShouldFollow(extent, viewport, offset));

    /// <summary>The decision has to be taken before the new content is measured.</summary>
    /// <remarks>
    /// The failure this exists to describe: asked a turn later, the extent has grown by the height of
    /// whatever just arrived, so the reader who <em>was</em> at the bottom now looks scrolled up by
    /// exactly that much — and a transcript that followed perfectly well stops following, for good,
    /// the moment a message is taller than the threshold.
    /// </remarks>
    [Fact]
    public void A_message_taller_than_the_threshold_would_break_a_late_decision()
    {
        const double viewport = 500, extent = 2000, atTheBottom = 1500;

        Assert.True(TranscriptFollow.ShouldFollow(extent, viewport, atTheBottom));

        // The same reader, the same offset, one message of 200px later.
        Assert.False(TranscriptFollow.ShouldFollow(extent + 200, viewport, atTheBottom));
    }
}
