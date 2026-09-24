using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels.AgentConversation;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// A message of the user's is drawn whole: it wraps inside its bubble rather than being cut off at the
/// bubble's edge.
/// </summary>
/// <remarks>The bubble on its own, reflowing when the tile narrows. What made it come out cut in the
/// running application is a measure and an arrange that disagree about the width; that is
/// <c>ReflowPanel</c>'s to repair and <c>ReflowPanelTests</c>' to pin. Staging it through a whole tile
/// here was tried and thrown away: a headless window heals the mismatch on its next pass whatever the
/// panel does, so the test passed with the repair and without it, and it answered differently depending
/// on which other view tests had run first.</remarks>
public class MessageBubbleTests
{
    [Fact]
    public void A_long_message_wraps_inside_its_bubble() => Ui.Run(() =>
    {
        // Long enough that no font this test could be run under fits it on one line at the narrow width:
        // the headless session is shared, another class's theme can be gone by the time this runs, and a
        // message that happened to fit made this fail by running order rather than by behaviour.
        const string said = "jaka ostatnia zmiane wykonalismy wg commit ? and a good deal more text after "
                            + "it, long enough that the tile has no width at which this could be drawn on "
                            + "a single line, whichever font it happens to be set in";

        using var settings = new TempSettings();
        using var tile = ConversationTiles.New(settings);

        var view = new AgentConversationTileView { DataContext = tile };
        using var theme = new HeadlessTheme();
        var window = new Window { Content = view, Width = 1560, Height = 400 };

        try
        {
            window.Show();
            Settle(window);
            tile.Draw(ConversationReducer.Replay([new UserMessageAdded("m1", said, [])]));
            Settle(window);
            var wide = Bubble(view).Bounds;

            // Narrowed until the message cannot be one line: the row has to grow with it. What it must not
            // do is keep the height of the line it worked out and let the rest fall under the tile's clip.
            window.Width = 520;
            Settle(window);
            var narrow = Bubble(view).Bounds;

            Assert.True(narrow.Height > wide.Height,
                $"the message did not wrap when the tile narrowed: {wide} then {narrow}");
            Assert.True(narrow.Width <= view.Bounds.Width,
                $"the bubble is wider than the tile: {narrow}, tile {view.Bounds.Width}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Runs the queued work and then the layout pass it asked for, and the work that pass
    /// queued in turn.</summary>
    /// <remarks>The pass is forced rather than waited for: headless, a layout is carried out on the render
    /// timer's tick, which runs on the wall clock — so on a loaded CI runner <c>RunJobs</c> alone could
    /// return before the list had made a container for the message, and the bubble was not there to be
    /// found.</remarks>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The block a message of the user's is drawn in.</summary>
    /// <remarks>Asked for by its class rather than by the control inside it: what draws the text is the
    /// markdown view both sides of the conversation now share, and its innards are its own business.
    /// </remarks>
    private static Border Bubble(Control view) =>
        view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("row-user"));
}
