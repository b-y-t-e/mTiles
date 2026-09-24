using System.Collections.ObjectModel;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The transcript's window: the tail of the conversation, grown upward, mirroring every change.
/// </summary>
public class TranscriptWindowTests
{
    private static ObservableCollection<int> Numbers(int count) => [.. Enumerable.Range(0, count)];

    private static int[] Drawn(TranscriptWindow window) => window.Visible.Cast<int>().ToArray();

    [Theory]
    [InlineData(3, 0)]   // shorter than the tail: all of it
    [InlineData(4, 0)]   // exactly the tail
    [InlineData(10, 6)]  // longer: only the last four
    public void A_conversation_opens_on_its_tail(int count, int start)
    {
        var window = new TranscriptWindow(tail: 4, page: 3);
        window.Show(Numbers(count));

        Assert.Equal(start, window.Start);
        Assert.Equal(Enumerable.Range(start, count - start), Drawn(window));
    }

    [Fact]
    public void Earlier_entries_are_drawn_a_page_at_a_time_until_there_are_none()
    {
        var window = new TranscriptWindow(tail: 4, page: 3);
        window.Show(Numbers(10));

        Assert.Equal(3, window.ShowEarlier());
        Assert.Equal(Enumerable.Range(3, 7), Drawn(window));
        Assert.Equal(3, window.ShowEarlier());
        Assert.Equal(0, window.Start);
        Assert.False(window.HasEarlier);
        Assert.Equal(0, window.ShowEarlier());
    }

    [Fact]
    public void Changes_inside_the_window_land_in_it_and_changes_above_it_only_move_it()
    {
        var source = Numbers(10);
        var window = new TranscriptWindow(tail: 4, page: 3) { MayTrim = () => false };
        window.Show(source);

        source.Add(10);
        Assert.Equal([6, 7, 8, 9, 10], Drawn(window));

        source[8] = 80;
        source[1] = 100; // above the window: nothing drawn changes
        Assert.Equal([6, 7, 80, 9, 10], Drawn(window));

        source.Insert(0, -1);
        Assert.Equal(7, window.Start);
        source.RemoveAt(0);
        Assert.Equal(6, window.Start);
        Assert.Equal([6, 7, 80, 9, 10], Drawn(window));

        source.RemoveAt(source.Count - 1);
        Assert.Equal([6, 7, 80, 9], Drawn(window));
        Assert.Equal(source.Skip(window.Start), Drawn(window));
    }

    [Theory]
    [InlineData(true, 7)]    // following the end: held at tail + page, the overflow given back one at a time
    [InlineData(false, 11)]  // reading back: nothing scrolled through is taken away
    public void A_window_following_the_end_gives_its_top_back(bool atEnd, int drawn)
    {
        var source = new ObservableCollection<int>();
        var window = new TranscriptWindow(tail: 4, page: 3) { MayTrim = () => atEnd };
        window.Show(source);

        for (var i = 0; i < 11; i++) source.Add(i);

        Assert.Equal(drawn, window.Visible.Count);
        Assert.Equal(source.Skip(window.Start), Drawn(window));
    }

    [Fact]
    public void A_cleared_and_refilled_conversation_keeps_as_much_as_was_drawn()
    {
        var source = Numbers(20);
        var window = new TranscriptWindow(tail: 4, page: 3);
        window.Show(source);
        window.ShowEarlier();

        source.Clear();
        Assert.Empty(window.Visible);
        Assert.Equal(0, window.Start);

        var other = Numbers(3);
        window.Show(other);
        Assert.Equal([0, 1, 2], Drawn(window));

        source.Add(1); // the collection it no longer shows
        Assert.Equal([0, 1, 2], Drawn(window));
    }

    [Fact]
    public void A_conversation_opened_while_reading_back_is_drawn_from_its_tail()
    {
        var source = new ObservableCollection<int>();
        var window = new TranscriptWindow(tail: 4, page: 3) { MayTrim = () => false };
        window.Show(source);

        for (var i = 0; i < 50; i++) source.Add(i);
        window.ShowTail();

        Assert.Equal([46, 47, 48, 49], Drawn(window));
    }

    [Fact]
    public void A_conversation_drawn_entry_by_entry_while_opening_never_holds_more_than_its_tail()
    {
        var source = new ObservableCollection<int>();
        var window = new TranscriptWindow(tail: 4, page: 3) { MayTrim = () => false };
        window.Show(source);
        var largest = 0;
        window.Visible.CollectionChanged += (_, _) => largest = Math.Max(largest, window.Visible.Count);

        window.BeginOpening();
        for (var i = 0; i < 50; i++) source.Add(i);
        window.ShowTail();

        Assert.True(largest <= 5);
        Assert.Equal([46, 47, 48, 49], Drawn(window));
    }

    [Fact]
    public void A_paused_window_catches_up_without_taking_down_what_it_drew()
    {
        var source = Numbers(20);
        var window = new TranscriptWindow(tail: 4, page: 3);
        window.Show(source);
        window.ShowEarlier();
        var start = window.Start;

        window.Pause();
        source.Add(20);
        source.Add(21);
        Assert.Equal(Enumerable.Range(start, 20 - start), Drawn(window));

        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        window.Visible.CollectionChanged += (_, e) => changes.Add(e.Action);
        window.Show(source);

        Assert.Equal(start, window.Start);
        Assert.Equal(source.Skip(start), Drawn(window));
        Assert.All(changes, a => Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Add, a));
    }

    [Fact]
    public void A_window_held_for_an_opening_is_let_go_when_it_is_shown_something_else()
    {
        var window = new TranscriptWindow(tail: 4, page: 3) { MayTrim = () => false };
        window.BeginOpening();
        var source = new ObservableCollection<int>();
        window.Show(source);

        for (var i = 0; i < 10; i++) source.Add(i);

        Assert.Equal(10, window.Visible.Count);
    }
}
