using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using mTiles.Tests.AgentSessions;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The transcript's paging on a real scroller: pages drawn above the reader do not move them, and a
/// reader following the end neither pages in nor holds the whole conversation.
/// </summary>
public class TranscriptPagingTests
{
    private const double EntryHeight = 50;

    private sealed class Transcript
    {
        public required HeadlessTheme Theme { get; init; }
        public required Window Window { get; init; }
        public required ScrollViewer Scroll { get; init; }
        public required TranscriptPaging Paging { get; init; }
        public required ObservableCollection<int> Entries { get; init; }

        public TranscriptWindow Drawn => Paging.Window;

        /// <summary>The entry whose top edge is at or just above the top of the viewport.</summary>
        public int EntryAtTop => Drawn.Start + (int)(Scroll.Offset.Y / EntryHeight);

        public void Settle() => HeadlessTheme.Layout(Window);

        /// <summary>Scrolls as a reader does: a key first, so the anchor reads the move as theirs.</summary>
        public void ReaderScrollsTo(double offset)
        {
            Scroll.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp });
            Scroll.Offset = new Vector(0, offset);
            Settle();
        }

        public void Close()
        {
            Window.Close();
            Theme.Dispose();
        }
    }

    private static Transcript Open(int entries, double viewportHeight)
    {
        var theme = new HeadlessTheme();
        var source = new ObservableCollection<int>(Enumerable.Range(0, entries));
        var list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<int>((_, _) => new Border { Height = EntryHeight }),
            DataContext = source,
        };
        var scroll = new ScrollViewer { Content = new StackPanel { Children = { list } } };
        var anchor = TranscriptAnchor.Attach(scroll);
        var paging = TranscriptPaging.Attach(list, scroll, anchor, dc => dc as ObservableCollection<int>);
        var window = new Window { Content = scroll, Width = 400, Height = viewportHeight };
        window.Show();
        var transcript = new Transcript { Theme = theme, Window = window, Scroll = scroll, Paging = paging, Entries = source };
        transcript.Settle();
        anchor.GoToEnd();
        transcript.Settle();
        return transcript;
    }

    [Fact]
    public void A_page_drawn_above_the_reader_leaves_the_same_entry_at_the_top()
    {
        Ui.Run(() =>
        {
            var transcript = Open(entries: 200, viewportHeight: 500);
            try
            {
                Assert.Equal(200 - TranscriptWindow.DefaultTail, transcript.Drawn.Start);

                transcript.ReaderScrollsTo(3 * EntryHeight);

                Assert.Equal(200 - TranscriptWindow.DefaultTail - TranscriptWindow.DefaultPage,
                    transcript.Drawn.Start);
                Assert.Equal(200 - TranscriptWindow.DefaultTail + 3, transcript.EntryAtTop);
            }
            finally { transcript.Close(); }
        });
    }

    [Fact]
    public void A_reader_following_the_end_neither_pages_in_nor_keeps_what_scrolls_away()
    {
        Ui.Run(() =>
        {
            var transcript = Open(entries: 200, viewportHeight: 500);
            try
            {
                for (var k = 0; k < 100; k++)
                {
                    transcript.Entries.Add(200 + k);
                    transcript.Settle();
                }

                Assert.True(transcript.Drawn.Visible.Count <= TranscriptWindow.DefaultTail + TranscriptWindow.DefaultPage);
                Assert.Equal(299, transcript.Drawn.Visible.Cast<int>().Last());
            }
            finally { transcript.Close(); }
        });
    }

    [Fact]
    public void A_list_taken_out_of_the_window_pauses_and_comes_back_with_the_pages_it_had()
    {
        Ui.Run(() =>
        {
            var transcript = Open(entries: 200, viewportHeight: 500);
            try
            {
                transcript.ReaderScrollsTo(3 * EntryHeight);
                var start = transcript.Drawn.Start;
                var scroll = transcript.Window.Content;
                transcript.Window.Content = null;
                transcript.Entries.Add(200);

                Assert.DoesNotContain(200, transcript.Drawn.Visible.Cast<int>());

                transcript.Window.Content = scroll;
                transcript.Settle();

                Assert.True(transcript.Drawn.Start <= start);
                Assert.Equal(200, transcript.Drawn.Visible.Cast<int>().Last());
            }
            finally { transcript.Close(); }
        });
    }

    [Fact]
    public void A_tail_shorter_than_the_viewport_is_paged_until_there_is_something_to_scroll()
    {
        Ui.Run(() =>
        {
            var transcript = Open(entries: 200, viewportHeight: 2500);
            try
            {
                Assert.True(transcript.Scroll.Extent.Height > transcript.Scroll.Viewport.Height);
                Assert.True(transcript.Drawn.HasEarlier);
            }
            finally { transcript.Close(); }
        });
    }
}
