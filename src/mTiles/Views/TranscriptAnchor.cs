using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mTiles.Views;

/// <summary>
/// Keeps a transcript's reader where they were when its layout changes under them — the tile resized,
/// a message reflowed, a markdown view settling at its final height.
/// </summary>
/// <remarks>
/// <para>A <see cref="ScrollViewer"/> keeps its <em>offset</em> across a resize, and an offset is a
/// number of pixels, not a place in the text: narrow the tile and every message above the reader grows
/// taller, so the same offset lands somewhere earlier in the conversation — in practice a random spot.
/// Two things were lost that way. A reader at the bottom was no longer at the bottom, so
/// <see cref="TranscriptFollow"/> stopped following and the next message arrived off screen; and a
/// reader half way up lost the line they were reading.</para>
/// <para>So the position is remembered as <b>what</b> is at the top of the viewport rather than how far
/// down it is: either "the end", or the deepest element spanning the viewport's top edge together with
/// how far into it the edge fell, as a fraction of its height — a paragraph that reflows to twice its
/// height still has the reader the same share of the way through it. It is taken whenever the reader
/// (or a scroll of ours) moves the offset with nothing else changing, and put back whenever the extent
/// or the viewport changes, which is every change that is not somebody scrolling.</para>
/// <para>The whole chain of elements down to that one is kept, not only the deepest: a markdown view may
/// rebuild its inner visuals when it reflows, and then the deepest is gone while its message is still
/// there — the nearest survivor is the next best place.</para>
/// </remarks>
public sealed class TranscriptAnchor
{
    /// <summary>How close to a number still counts as being it, in pixels a scroller rounds.</summary>
    private const double Tolerance = 0.5;

    private readonly ScrollViewer _scroll;
    private bool _atEnd = true;
    private List<(Visual Element, double Fraction)> _chain = [];
    private bool _restoreQueued;
    private readonly ScrollWeMade _ourScroll = new();
    private bool _gesture;
    private IPointer? _held;

    private TranscriptAnchor(ScrollViewer scroll)
    {
        _scroll = scroll;
        scroll.ScrollChanged += OnScrollChanged;
        scroll.AttachedToVisualTree += (_, _) => QueueRestore();

        // A reader who moved did it with their hand. Everything else that moves an offset — a layout
        // settling, a bring-into-view, this anchor's own restore — is somebody else's, and the offset
        // it leaves behind is indistinguishable from theirs: a transcript opened at the end and then
        // laid out again as its markdown views found their final heights reported a move nobody made,
        // which became the anchor and left the reader part way up a conversation they had not touched.
        // Tunnelled and handledEventsToo, because the scroller's own handling of a wheel is what marks
        // the event handled before it would bubble back here.
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnGesture,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.PointerPressedEvent, OnPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.KeyDownEvent, OnGesture,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // A press that is still held is a hand still on the scroller. The scrollbar's thumb captures
        // the pointer, so a drag is one press and then nothing but offsets until the button comes back
        // up: spent like a wheel, the gesture would account for the drag's first pixel and every offset
        // after it would be read as somebody else's, so a message arriving mid-drag restored the anchor
        // taken where the drag began and threw the reader back there.
        scroll.AddHandler(InputElement.PointerReleasedEvent, OnReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The reader has touched something. See the constructor.</summary>
    private void OnGesture(object? sender, RoutedEventArgs e) => _gesture = true;

    /// <summary>A button is down: this pass is the reader's, and so is every pass until it comes up.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _gesture = true;
        _held = e.Pointer;
    }

    /// <summary>The hand is off. The release's own pass is still theirs, hence the gesture left owing.</summary>
    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _gesture = true;
        _held = null;
    }

    /// <summary>Whether the hand that pressed is still on the scroller.</summary>
    /// <remarks>Asked of the pointer itself rather than remembered as a flag a release puts down.
    /// <c>PointerCaptureLost</c> is a direct event, raised on the element that held the capture — the
    /// scrollbar's thumb — so a handler on the scroller never runs, and a drag that ends without a
    /// release (a dialog opening, the window deactivating, Alt+Tab) left the flag latched: from then on
    /// every layout pass counted as a gesture and the anchor was taken on passes nobody made, which is
    /// the very fault this reads the pointer to avoid. A thumb drag holds the capture for as long as the
    /// button is down and gives it up however the drag ends, so the capture is the state itself rather
    /// than a report of it going away.</remarks>
    private bool IsHeld => _held?.Captured is not null;

    /// <summary>Starts keeping <paramref name="scroll"/>'s reader in place for as long as it lives.</summary>
    public static TranscriptAnchor Attach(ScrollViewer scroll) => new(scroll);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // A tile taking the whole workspace, or giving it back, is drawn by the splits above it showing
        // one child: this scroller is detached from the visual tree and put back a moment later. While
        // it is out there is no viewport and nothing is laid out, so the scroller clamps its offset to
        // zero and reports a viewport and an extent of nothing — read as the reader moving, that
        // becomes the anchor and full screen opens at the top of the conversation; acted on as a
        // restore, the chain's elements have no height to take a share of. Such a pass says nothing
        // about where anybody is, so the anchor is left exactly as it was and put back on the first
        // pass that can be measured — which the re-attach raises, since the viewport comes back with it.
        if (!CanBeMeasured(_scroll.Viewport.Height, _scroll.Extent.Height)) return;

        // Taken here rather than left standing: the gesture accounts for the pass it arrived in, and a
        // wheel turned once must not excuse every layout pass after it.
        var gesture = _gesture || IsHeld;
        _gesture = false;
        if (gesture && ReaderMoved(e.OffsetDelta.Y, _scroll.Offset.Y, MaxOffset(), _ourScroll.Take())) Capture();

        // Nothing but the offset moved, which is somebody scrolling: there is nothing to put back.
        if (e.ExtentDelta == default && e.ViewportDelta == default) return;

        // After layout rather than inside it: the elements' own bounds are only final once the pass that
        // raised this has finished, and a late-settling message raises another change that lands here too.
        QueueRestore();
    }

    /// <summary>
    /// Puts the reader on the end and keeps them there, whatever they were anchored to.
    /// </summary>
    /// <remarks>What somebody sending a message has asked for. Reading back through a run and then
    /// saying something is the one move that means "I am done reading back": the answer is about to
    /// arrive at the bottom, and following it is the whole of what the transcript does — so the send
    /// is allowed to move the anchor where nothing else but the reader's own scrolling may. Said as an
    /// anchor rather than as a scroll of its own, because what has to end at the bottom is not this
    /// instant but the passes after it, as the message is measured and the answer streams in.</remarks>
    public void GoToEnd()
    {
        _atEnd = true;
        _chain = [];
        // Whatever the reader last touched is spent: this is a fresh transcript or a message just sent,
        // and a gesture still owed from before it would be answered by the first layout pass after.
        _gesture = false;
        QueueRestore();
    }

    /// <summary>Whether a reported scroll describes a scroller anybody is looking at.</summary>
    /// <remarks>A scroller with no viewport or no content is one that has been detached, collapsed or
    /// not yet laid out. Its offset is whatever the clamp left there and its elements have no bounds,
    /// so neither half of this anchor has anything to read.</remarks>
    public static bool CanBeMeasured(double viewport, double extent) => viewport > 0 && extent > 0;

    private void QueueRestore()
    {
        if (_restoreQueued) return;
        _restoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _restoreQueued = false;
            if (!CanBeMeasured(_scroll.Viewport.Height, _scroll.Extent.Height)) return;
            Restore();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Whether an offset that moved was moved by the reader.
    /// </summary>
    /// <param name="offsetDelta">How far the offset moved in this pass.</param>
    /// <param name="offset">Where it ended up.</param>
    /// <param name="maxOffset">The furthest down the content now allows.</param>
    /// <param name="offsetWeWrote">Where this anchor put it in the scroll this pass reports, or null
    /// when the pass has no scroll of ours to account for.</param>
    /// <remarks>
    /// <para>Asked only of a pass the reader's own hand reached — a wheel, a press, a key. This rule
    /// tells three <em>offset changes</em> apart and cannot tell a move nobody made from one somebody
    /// did, so the gesture is the question asked before it.</para>
    /// <para>A reader scrolling in the same pass the layout changed — a wheel turned while a message
    /// streams in — has made a move of their own, and it becomes the anchor, or the restore would pull
    /// them back. Two other things arrive looking exactly like it and neither is theirs.</para>
    /// <para>The offset being <b>clamped</b> by content that grew shorter is a move nobody made. It only
    /// ever pulls the offset back and lands it on the new bottom, so the sign together with the landing
    /// separates the two rather than the distance from the end: judged by distance alone, a reader
    /// wheeling onto the very last line while the extent grew was read as a clamp and put back one turn
    /// of the wheel short of the bottom.</para>
    /// <para><b>This anchor's own scroll</b> is the other, and it is positive, so the sign says nothing
    /// about it. <see cref="Restore"/> scrolls, and a pass where that scroll and a growing extent land
    /// together used to be read as a reader moving away from a new end — pinning the anchor to the old
    /// one and leaving somebody who never scrolled unable to follow the answer again. What it is judged
    /// by is where the offset ended up, not what happened to it: a scroller merges the moves of a pass
    /// into one delta and raises this afterwards, so the delta cannot be attributed while the resting
    /// place can.</para>
    /// </remarks>
    public static bool ReaderMoved(double offsetDelta, double offset, double maxOffset, double? offsetWeWrote)
    {
        if (offsetDelta == 0) return false;
        if (offsetWeWrote is { } ours && Math.Abs(offset - ours) <= Tolerance) return false;
        return offsetDelta > 0 || offset < maxOffset - Tolerance;
    }

    private double MaxOffset() => Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);

    private void Capture()
    {
        _atEnd = TranscriptFollow.ShouldFollow(
            _scroll.Extent.Height, _scroll.Viewport.Height, _scroll.Offset.Y);
        _chain = _atEnd ? [] : ChainAt(_scroll.Offset.Y);
    }

    private void Restore()
    {
        if (_atEnd)
        {
            _scroll.ScrollToEnd();
            RememberOurOffset();
            return;
        }

        if (_scroll.Content is not Visual content) return;
        for (var i = _chain.Count - 1; i >= 0; i--)
        {
            var (element, fraction) = _chain[i];
            if (TopWithin(element, content) is not { } top) continue;

            var y = Math.Clamp(OffsetFor(top, element.Bounds.Height, fraction), 0, MaxOffset());
            _scroll.Offset = _scroll.Offset.WithY(y);
            RememberOurOffset();
            return;
        }
    }

    /// <summary>Notes where this anchor has just left the offset, read back rather than assumed: the
    /// scroller clamps what it is given, and a remembered value it never reached would let the next
    /// pass's real reader move be taken for ours.</summary>
    private void RememberOurOffset() => _ourScroll.Note(_scroll.Offset.Y);

    /// <summary>Where the viewport's top goes to put it <paramref name="fraction"/> of the way into an
    /// element that now starts at <paramref name="top"/> and is <paramref name="height"/> tall.</summary>
    public static double OffsetFor(double top, double height, double fraction) =>
        top + Math.Clamp(fraction, 0, 1) * Math.Max(0, height);

    /// <summary>How far into an element the viewport's top falls, as a share of its height.</summary>
    public static double FractionOf(double top, double height, double offset) =>
        height <= 0 ? 0 : Math.Clamp((offset - top) / height, 0, 1);

    /// <summary>The elements spanning <paramref name="offset"/>, outermost first, each with how far into
    /// it that line falls.</summary>
    private List<(Visual, double)> ChainAt(double offset)
    {
        var chain = new List<(Visual, double)>();
        if (_scroll.Content is not Visual content) return chain;

        var node = content;
        while (true)
        {
            var children = node.GetVisualChildren().Where(c => c.IsVisible && c.Bounds.Height > 0).ToList();
            if (SpanningChild(children, content, offset) is { } spanning)
            {
                chain.Add((spanning.Element, FractionOf(spanning.Top, spanning.Element.Bounds.Height, offset)));
                node = spanning.Element;
                continue;
            }

            // The line fell in a gap between two children — a margin or the list's spacing. Anchored to
            // the container instead, the fraction would be of the whole transcript and every message
            // added below would move the reader; the neighbour is the place they were looking at.
            if (NeighbourAcrossGap(children, content, offset) is { } neighbour) chain.Add(neighbour);
            return chain;
        }
    }

    private static (Visual Element, double Top)? SpanningChild(
        IEnumerable<Visual> children, Visual content, double offset)
    {
        foreach (var child in children)
            if (TopWithin(child, content) is { } top && top <= offset && offset < top + child.Bounds.Height)
                return (child, top);
        return null;
    }

    /// <summary>The first child below the gap, from its first line; or, past the last one, the last
    /// child from its end.</summary>
    private static (Visual, double)? NeighbourAcrossGap(
        IEnumerable<Visual> children, Visual content, double offset)
    {
        (Visual, double)? above = null;
        foreach (var child in children)
        {
            if (TopWithin(child, content) is not { } top) continue;
            if (top > offset) return (child, 0);
            above = (child, 1);
        }
        return above;
    }

    private static double? TopWithin(Visual element, Visual content) =>
        element.TranslatePoint(default, content)?.Y;
}

/// <summary>
/// A scroll the anchor made itself, waiting for the one pass that reports it.
/// </summary>
/// <remarks>
/// Spent by that pass whether or not it turned out to be the excuse, because kept beyond it the offset
/// goes on answering for the reader's own moves for the life of the tile: somebody at the end who reads
/// back and then wheels down to the bottom again lands on the very offset the anchor left there, is
/// taken for the anchor a second time, and is never seen to be following the end again — so from then
/// on every new message pulls them off the bottom and into the middle of the transcript.
/// </remarks>
internal sealed class ScrollWeMade
{
    private double? _offset;

    /// <summary>Records the offset the anchor has just left, replacing any earlier one unspent.</summary>
    public void Note(double offset) => _offset = offset;

    /// <summary>The offset still to be accounted for, if there is one, and forgets it.</summary>
    public double? Take()
    {
        var ours = _offset;
        _offset = null;
        return ours;
    }
}
