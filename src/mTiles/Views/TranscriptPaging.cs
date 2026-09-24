using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;

namespace mTiles.Views;

/// <summary>
/// Hands a transcript's list the tail of its conversation and draws more of it as the reader nears
/// the top.
/// </summary>
/// <remarks>
/// <para>The window is <see cref="TranscriptWindow"/>'s; this is what moves it. Nothing here keeps the
/// reader in place when entries appear above them — <see cref="TranscriptAnchor"/> already does that for
/// every change of height, and a page drawn above the viewport is one: the element at the top of the
/// screen is still there, so it is put back at the top.</para>
/// <para>Near the top means within a screen of it, so the next page is drawn before the reader arrives
/// rather than when they have stopped at an edge. It is asked again after each page, which is also what
/// fills a tall tile whose tail is shorter than the viewport: a conversation of short messages opens on
/// as many pages as it takes to give the scroller something to scroll.</para>
/// <para>The list's <c>ItemsSource</c> is written here and nowhere else — the markup binds none — since
/// a binding and this writing the same property would leave whichever ran last in charge.</para>
/// </remarks>
public sealed class TranscriptPaging
{
    private readonly ScrollViewer _scroll;
    private readonly TranscriptAnchor _anchor;
    private readonly TranscriptWindow _window;
    private bool _queued;

    private TranscriptPaging(ItemsControl list, ScrollViewer scroll, TranscriptAnchor anchor,
        Func<object?, IList?> sourceOf)
    {
        _scroll = scroll;
        _anchor = anchor;
        // Given back only from a window the reader is watching the end of, with more than a screen above
        // it: what goes is out of sight, and what stays still gives the scroller something to scroll.
        _window = new TranscriptWindow { MayTrim = () => anchor.IsAtEnd && ScreensAbove > 1 };
        list.ItemsSource = _window.Visible;
        list.DataContextChanged += (_, _) => _window.Show(sourceOf(list.DataContext));
        // A tile moved in the layout gets a new view while its view model lives on, so a list that has
        // left the visual tree lets go of the collection — or the abandoned view stays reachable from it
        // and goes on mirroring every entry for the life of the tile. What it drew stays, so a list put back
        // (a tile maximised, dragged, its neighbour split) returns with the pages the reader had.
        list.DetachedFromVisualTree += (_, _) => _window.Pause();
        list.AttachedToVisualTree += (_, _) => _window.Show(sourceOf(list.DataContext));
        _window.Show(sourceOf(list.DataContext));
        scroll.ScrollChanged += (_, _) => QueueEarlier();
    }

    /// <summary>Starts paging <paramref name="list"/> over whatever <paramref name="sourceOf"/> reads out
    /// of its data context.</summary>
    public static TranscriptPaging Attach(ItemsControl list, ScrollViewer scroll, TranscriptAnchor anchor,
        Func<object?, IList?> sourceOf) => new(list, scroll, anchor, sourceOf);

    /// <summary>The window the list is drawing.</summary>
    public TranscriptWindow Window => _window;

    /// <summary>How many screens of transcript lie above the viewport.</summary>
    private double ScreensAbove => _scroll.Offset.Y / Math.Max(1, _scroll.Viewport.Height);

    private bool Scrollable => _scroll.Extent.Height > _scroll.Viewport.Height;

    /// <summary>A reader following the end is paged only to fill a screen that has nothing to scroll;
    /// otherwise every append that trims the top would page it straight back in.</summary>
    private bool NearTheTop =>
        _window.HasEarlier
        && TranscriptAnchor.CanBeMeasured(_scroll.Viewport.Height, _scroll.Extent.Height)
        && _scroll.Offset.Y < _scroll.Viewport.Height
        && (!_anchor.IsAtEnd || !Scrollable);

    private void QueueEarlier()
    {
        if (_queued || !NearTheTop) return;
        _queued = true;
        // After the anchor's own restore, which runs at Loaded: a page drawn before it would be measured
        // against an offset the anchor is about to move.
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (NearTheTop) _window.ShowEarlier();
        }, DispatcherPriority.Background);
    }
}
