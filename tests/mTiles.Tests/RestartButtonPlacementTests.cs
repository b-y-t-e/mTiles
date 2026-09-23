using System.ComponentModel;
using mTiles.Models;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Where a tile's Restart is drawn — a button in the header, or the overflow menu alone.
/// </summary>
/// <remarks>
/// The header used to answer this by comparing the leaf's kind id against the Agent tile's, which made
/// "a second kind whose restart is a cold resume" an edit to a view rather than an extension of a kind,
/// and left the rule where no test could reach it. The answer is the action's own, so it is pinned here.
/// </remarks>
public sealed class RestartButtonPlacementTests
{
    /// <summary>A shell's restart is pressed every few minutes, so it keeps its button.</summary>
    [Fact]
    public void A_restart_reached_often_is_a_header_button()
    {
        var leaf = Leaf(new RestartingTile(preferOverflow: false));

        Assert.True(leaf.CanRestart);
        Assert.True(leaf.RestartHasHeaderButton);
    }

    /// <summary>A cold resume of a conversation is not, and the tile says so itself.</summary>
    [Fact]
    public void A_restart_the_tile_leaves_to_the_menu_gets_no_button()
    {
        var leaf = Leaf(new RestartingTile(preferOverflow: true));

        Assert.False(leaf.RestartHasHeaderButton);
    }

    /// <summary>Leaving it to the menu is about where it is drawn, never about whether it can be
    /// done: the menu entry and Ctrl+Shift+R both read <see cref="LeafTileNodeViewModel.CanRestart"/>,
    /// which must stay true.</summary>
    [Fact]
    public void A_restart_left_to_the_menu_can_still_be_done()
    {
        Assert.True(Leaf(new RestartingTile(preferOverflow: true)).CanRestart);
    }

    /// <summary>A tile with nothing to restart has neither.</summary>
    [Fact]
    public void A_tile_that_restarts_nothing_has_no_button()
    {
        var leaf = Leaf(new RestartingTile(preferOverflow: false, offersRestart: false));

        Assert.False(leaf.CanRestart);
        Assert.False(leaf.RestartHasHeaderButton);
    }

    private static LeafTileNodeViewModel Leaf(ITile content) =>
        new(TileKindIds.Terminal, content, "", new TileActivationScope());

    private sealed class RestartingTile(bool preferOverflow, bool offersRestart = true) : ITileActions
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string KindId => "stub";

        public IReadOnlyList<TileAction> Actions => offersRestart
            ? [new(TileActionIds.Restart, "Restart", "restart", PreferOverflow: preferOverflow)]
            : [];

        public Task<TileActionResult> InvokeAsync(string id) => Task.FromResult(TileActionResult.Ok);

        public void Dispose() { }
    }
}
