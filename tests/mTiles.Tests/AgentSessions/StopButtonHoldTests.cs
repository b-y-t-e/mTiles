using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Stop cannot be pressed by the click that sent the message.
/// </summary>
/// <remarks>Send and Stop are one slot — the button becomes the other the moment the turn begins — so a
/// double click is a turn started and stopped before the agent has said a word, with nothing on screen
/// explaining what happened. The window is the double-click one and no longer, because stopping is the
/// one thing here somebody wants urgently. Each hold's end is released by the test rather than waited
/// out, so nothing here races a real clock.</remarks>
public class StopButtonHoldTests
{
    private static (AgentConversationTileViewModel Tile, Queue<Action> Holds) Tile()
    {
        var tile = ConversationTiles.New(new TempSettings());
        var holds = new Queue<Action>();
        tile.AfterStopButtonHold = holds.Enqueue;
        return (tile, holds);
    }

    [Fact]
    public void A_send_holds_the_stop_button_until_its_window_ends()
    {
        var (tile, holds) = Tile();
        using var _ = tile;
        Assert.True(tile.InterruptCommand.CanExecute(null));

        tile.HoldTheStopButton();

        Assert.False(tile.CanInterrupt);
        Assert.False(tile.InterruptCommand.CanExecute(null));

        holds.Dequeue()();

        Assert.True(tile.CanInterrupt);
        Assert.True(tile.InterruptCommand.CanExecute(null));
    }

    /// <summary>Only the send that armed the hold releases it.</summary>
    /// <remarks>Two messages in quick succession: the first one's timer would otherwise unlock the
    /// button under the second, which is the very press this exists to catch.</remarks>
    [Fact]
    public void A_second_send_keeps_the_button_held_past_the_first_ones_window()
    {
        var (tile, holds) = Tile();
        using var _ = tile;

        tile.HoldTheStopButton();
        tile.HoldTheStopButton();
        var first = holds.Dequeue();
        var second = holds.Dequeue();

        first();
        Assert.False(tile.CanInterrupt);

        second();
        Assert.True(tile.CanInterrupt);
    }
}
