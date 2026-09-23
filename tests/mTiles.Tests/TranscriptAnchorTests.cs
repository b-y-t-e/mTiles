using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Where a reader half way through a message goes when that message reflows under them.
/// </summary>
public class TranscriptAnchorTests
{
    [Theory]
    // The viewport's top a quarter of the way into a 400px paragraph.
    [InlineData(1000, 400, 1100, 0.25)]
    // At its first line, and past its end (clamped — the edge is in the next element then).
    [InlineData(1000, 400, 1000, 0)]
    [InlineData(1000, 400, 1500, 1)]
    // An element with no height has no inside to be part of the way through.
    [InlineData(1000, 0, 1000, 0)]
    public void The_share_is_taken_of_the_element(double top, double height, double offset, double expected) =>
        Assert.Equal(expected, TranscriptAnchor.FractionOf(top, height, offset), 6);

    [Fact]
    public void A_paragraph_that_doubles_keeps_the_reader_the_same_share_of_the_way_through()
    {
        var fraction = TranscriptAnchor.FractionOf(top: 1000, height: 400, offset: 1100);

        // Narrowed: everything above grew too, so the paragraph starts lower and is twice as tall.
        Assert.Equal(2200, TranscriptAnchor.OffsetFor(top: 2000, height: 800, fraction), 6);
    }

    /// <summary>
    /// Whose move an offset that changed was.
    /// </summary>
    /// <remarks>
    /// Three moves arrive at the anchor looking alike and only one of them is the reader's, whose place
    /// is the one worth keeping. The other two are the scroller clamping an offset that content shrinking
    /// left past the bottom, and the anchor's own scroll coming back round — read as the reader's, that
    /// one takes somebody who never scrolled off the end of a streaming answer for good, since the anchor
    /// then pins them to where the end used to be.
    /// </remarks>
    [Theory]
    // Nothing moved.
    [InlineData(0, 900, 900, null, false)]
    // The reader wheels down, and on, to the very last line while the extent grows in the same pass.
    [InlineData(120, 500, 900, null, true)]
    [InlineData(120, 900, 900, null, true)]
    // And back up, which is theirs wherever it lands.
    [InlineData(-300, 600, 900, null, true)]
    // Content grew shorter: the offset was past the bottom and the scroller pulled it onto the new one.
    [InlineData(-40, 900, 900, null, false)]
    // The anchor's own scroll to the end, landing where it put it — even as the extent grew past it.
    [InlineData(120, 900, 1400, 900.0, false)]
    // Its own restore into the middle, likewise, and within what a scroller rounds.
    [InlineData(-250, 600, 900, 600.2, false)]
    // The reader moving on from where the anchor left them is theirs again.
    [InlineData(60, 960, 1400, 900.0, true)]
    public void The_move_is_the_readers_only_when_nothing_else_accounts_for_it(
        double offsetDelta, double offset, double maxOffset, double? offsetWeWrote, bool expected) =>
        Assert.Equal(expected, TranscriptAnchor.ReaderMoved(offsetDelta, offset, maxOffset, offsetWeWrote));

    /// <summary>
    /// The anchor's own scroll answers for one pass and no more.
    /// </summary>
    /// <remarks>
    /// The move it must not go on excusing is the reader coming back to the bottom: that lands on the
    /// very offset the anchor last wrote there, so a memory kept past the pass it belongs to swallows
    /// the move — and the reader standing at the end is never seen to be following it again, with every
    /// new message from then on pulling them into the middle of the transcript.
    /// </remarks>
    [Fact]
    public void The_anchors_own_scroll_is_accounted_for_once()
    {
        var ourScroll = new ScrollWeMade();

        Assert.Null(ourScroll.Take());

        ourScroll.Note(900);
        Assert.Equal(900, ourScroll.Take());

        // The reader wheeling back down to that same offset is theirs, not ours again.
        Assert.Null(ourScroll.Take());
        Assert.True(TranscriptAnchor.ReaderMoved(
            offsetDelta: 300, offset: 900, maxOffset: 900, offsetWeWrote: ourScroll.Take()));
    }

    /// <summary>
    /// A scroller nobody is looking at says nothing about where the reader is.
    /// </summary>
    /// <remarks>
    /// Maximizing a tile, and putting the layout back, detaches this scroller from the visual tree and
    /// re-attaches it. While it is out its offset is clamped to zero and its elements have no bounds, so
    /// a pass reported then would either make the top of the conversation the anchor or restore onto
    /// heights of nothing.
    /// </remarks>
    [Theory]
    [InlineData(600, 2400, true)]
    // Detached, or collapsed: no viewport.
    [InlineData(0, 2400, false)]
    // Attached, not yet laid out: no content.
    [InlineData(600, 0, false)]
    [InlineData(0, 0, false)]
    public void A_pass_counts_only_while_there_is_something_to_measure(
        double viewport, double extent, bool expected) =>
        Assert.Equal(expected, TranscriptAnchor.CanBeMeasured(viewport, extent));
}
