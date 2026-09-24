using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace mTiles.Views;

/// <summary>
/// The tail of a transcript, which is all of it a list is handed to draw.
/// </summary>
/// <remarks>
/// <para>Neither transcript virtualises: each is an <c>ItemsControl</c> in a <c>StackPanel</c> in the
/// tile's one scroller, which hands the list an unbounded height, so a virtualising panel would realise
/// every item anyway. And every message costs a <c>MarkdownViewer</c>, which is the expensive control in
/// either tile, so a long conversation opened with every one of them built and laid out. So the list is
/// given a <em>window</em> onto the view model's collection instead of the collection itself: the last
/// <see cref="Tail"/> entries, grown upward a <see cref="Page"/> at a time as the reader nears the top
/// (<see cref="TranscriptPaging"/>). A chat is read from the bottom, and this is that, the other way up
/// from how a list usually pages.</para>
/// <para>The view model's collection stays whole — the copy builders, the sent-message history and the
/// seams all read it — so this is a view's concern and knows nothing of either tile. It mirrors every
/// change the collection raises: one inside the window lands at the same place in it, one before the
/// window only moves where the window starts.</para>
/// <para><b>A window following the end gives its top back</b> (<see cref="MayTrim"/>): an agent left
/// running all afternoon appends for ever, and a reader watching the end would otherwise end up holding
/// the whole conversation again. Only while the reader is at the end, because what goes is out of sight
/// above them; a reader part way up keeps everything they have scrolled through.</para>
/// </remarks>
public sealed class TranscriptWindow
{
    /// <summary>How many entries a conversation opens with.</summary>
    public const int DefaultTail = 40;

    /// <summary>How many entries each step upward adds.</summary>
    public const int DefaultPage = 30;

    private IList? _source;
    private INotifyCollectionChanged? _notifier;
    private bool _opening;

    public TranscriptWindow(int tail = DefaultTail, int page = DefaultPage)
    {
        Tail = Math.Max(1, tail);
        Page = Math.Max(1, page);
    }

    public int Tail { get; }

    public int Page { get; }

    /// <summary>What the list draws: the source from <see cref="Start"/> to its end.</summary>
    public ObservableCollection<object?> Visible { get; } = [];

    /// <summary>The index in the source of the first entry drawn.</summary>
    public int Start { get; private set; }

    /// <summary>Whether there is anything above what is drawn.</summary>
    public bool HasEarlier => Start > 0;

    /// <summary>Whether the top of the window may be given back now — asked when it has grown past a
    /// page more than it opens with.</summary>
    public Func<bool> MayTrim { get; set; } = () => true;

    /// <summary>Starts mirroring <paramref name="source"/>, opening on its tail.</summary>
    public void Show(IList? source)
    {
        if (ReferenceEquals(source, _source))
        {
            Resume();
            return;
        }
        Pause();
        // A window held at its tail for a conversation being opened belongs to that conversation.
        _opening = false;
        _source = source;
        Subscribe();
        Rebuild(Tail);
    }

    /// <summary>Stops mirroring the source while keeping what is drawn and where it starts — for a list
    /// taken out of the visual tree that may be put back, which must come back as the reader left it.</summary>
    public void Pause()
    {
        if (_notifier is not null) _notifier.CollectionChanged -= OnSourceChanged;
        _notifier = null;
    }

    /// <summary>Mirrors the source again after <see cref="Pause"/>, catching up on whatever changed
    /// meanwhile without cutting the window back to its tail.</summary>
    private void Resume()
    {
        if (_notifier is not null || _source is null) return;
        Subscribe();
        CatchUp();
    }

    /// <summary>Brings the window back in step with a source that moved while nobody was listening,
    /// keeping every drawn entry that is still where it was.</summary>
    /// <remarks>Entry by entry rather than drawn again: a rebuild takes every container with it — every
    /// <c>MarkdownViewer</c>, which is the cost the window exists to avoid, and every element
    /// <see cref="TranscriptAnchor"/> is holding the reader's place by. While a list is out of the tree
    /// the usual change is a few entries appended, which leaves the whole of what was drawn standing.
    /// </remarks>
    private void CatchUp()
    {
        if (_source is null) return;
        var count = _source.Count;
        if (Start > count)
        {
            Rebuild(Math.Max(Tail, Visible.Count));
            return;
        }

        var same = 0;
        var comparable = Math.Min(Visible.Count, count - Start);
        while (same < comparable && Equals(Visible[same], _source[Start + same])) same++;
        while (Visible.Count > same) Visible.RemoveAt(Visible.Count - 1);
        for (var i = Start + same; i < count; i++) Visible.Add(_source[i]);
    }

    private void Subscribe()
    {
        _notifier = _source as INotifyCollectionChanged;
        if (_notifier is not null) _notifier.CollectionChanged += OnSourceChanged;
    }


    /// <summary>Draws the tail again and nothing above it — for a conversation just opened, which must
    /// not arrive drawn whole because the reader happened to be scrolled back in the one before.</summary>
    public void ShowTail()
    {
        _opening = false;
        Rebuild(Tail);
    }

    /// <summary>Holds the window at its tail, whatever the reader was doing, until <see cref="ShowTail"/> —
    /// for a conversation about to be drawn over this one entry by entry, which would otherwise build every
    /// entry of it before being cut back to the tail.</summary>
    public void BeginOpening() => _opening = true;

    /// <summary>Draws up to a page more above what is drawn, and answers how many.</summary>
    public int ShowEarlier()
    {
        if (_source is null) return 0;
        var count = Math.Min(Page, Start);
        for (var k = 1; k <= count; k++) Visible.Insert(0, _source[Start - k]);
        Start -= count;
        return count;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is { } added:
                if (e.NewStartingIndex < Start) Start += added.Count;
                else
                    for (var k = 0; k < added.Count; k++)
                        Visible.Insert(e.NewStartingIndex - Start + k, added[k]);
                Trim();
                break;

            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is { } removed:
                var index = e.OldStartingIndex;
                var before = Math.Clamp(Start - index, 0, removed.Count);
                var at = Math.Max(index, Start) - Start;
                for (var k = before; k < removed.Count && at < Visible.Count; k++) Visible.RemoveAt(at);
                Start -= before;
                break;

            case NotifyCollectionChangedAction.Replace when e.NewStartingIndex >= 0 && e.NewItems is { } replaced:
                for (var k = 0; k < replaced.Count; k++)
                {
                    var i = e.NewStartingIndex + k - Start;
                    if (i >= 0 && i < Visible.Count) Visible[i] = replaced[k];
                }
                break;

            default:
                // A move, a reset, or a change that did not say where: drawn again from the source, as
                // much of it as was drawn — a reader who had scrolled back keeps that much — and at least
                // the tail, which is what a cleared-and-refilled conversation opens with.
                Rebuild(Math.Max(Tail, Visible.Count));
                break;
        }
    }

    private void Rebuild(int size)
    {
        Visible.Clear();
        var count = _source?.Count ?? 0;
        Start = Math.Max(0, count - size);
        for (var i = Start; i < count; i++) Visible.Add(_source![i]);
    }

    /// <summary>Gives back the top of a window that has grown past a page more than its tail, if it may —
    /// only the overflow, one entry per append, so the window never drops below what it held and never
    /// has to be paged back in to fill the screen.</summary>
    private void Trim()
    {
        var kept = _opening ? Tail : Tail + Page;
        if (Visible.Count <= kept || (!_opening && !MayTrim())) return;
        var excess = Visible.Count - kept;
        for (var k = 0; k < excess; k++) Visible.RemoveAt(0);
        Start += excess;
    }
}
