using mTiles.Services.Agents;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Stop cannot be pressed by the click that sent the message.
/// </summary>
/// <remarks>Send and Stop are one slot — the button becomes the other the moment the turn begins — so a
/// double click is a turn started and stopped before the agent has said a word, with nothing on screen
/// explaining what happened. The window is the double-click one and no longer, because stopping is the
/// one thing here somebody wants urgently.</remarks>
public class StopButtonHoldTests
{
    private static AgentConversationTileViewModel Tile()
    {
        var settings = new TempSettings();
        var agent = AiAgentCatalog.Find("claude")!;
        return new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
            new mTiles.AgentSessions.Storage.SqliteConversationStore(
                Path.Combine(Path.GetTempPath(), $"mtiles-stop-{Guid.NewGuid():N}.db")),
            AiAgentCatalog.SeedInstanceFor(agent), agent, () => "tile", post: action => action());
    }

    [Fact]
    public async Task A_send_holds_the_stop_button_for_the_double_click_window()
    {
        using var tile = Tile();
        Assert.True(tile.InterruptCommand.CanExecute(null));

        tile.HoldTheStopButton();

        Assert.False(tile.CanInterrupt);
        Assert.False(tile.InterruptCommand.CanExecute(null));

        await Task.Delay(AgentConversationTileViewModel.StopButtonHold + TimeSpan.FromMilliseconds(300));

        Assert.True(tile.CanInterrupt);
        Assert.True(tile.InterruptCommand.CanExecute(null));
    }

    /// <summary>Only the send that armed the hold releases it.</summary>
    /// <remarks>Two messages in quick succession: the first one's timer would otherwise unlock the
    /// button under the second, which is the very press this exists to catch.</remarks>
    [Fact]
    public async Task A_second_send_keeps_the_button_held_past_the_first_ones_window()
    {
        using var tile = Tile();

        tile.HoldTheStopButton();
        await Task.Delay(AgentConversationTileViewModel.StopButtonHold - TimeSpan.FromMilliseconds(150));
        tile.HoldTheStopButton();
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(tile.CanInterrupt);

        await Task.Delay(AgentConversationTileViewModel.StopButtonHold + TimeSpan.FromMilliseconds(300));

        Assert.True(tile.CanInterrupt);
    }
}
