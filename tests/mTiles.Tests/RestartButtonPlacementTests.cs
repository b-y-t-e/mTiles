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
    /// <summary>Whether the header draws Restart is the action's own answer; whether it can be done at all
    /// (the menu entry and Ctrl+Shift+R read <see cref="LeafTileNodeViewModel.CanRestart"/>) is not.</summary>
    [Theory]
    // A shell's restart is pressed every few minutes, so it keeps its button.
    [InlineData(false, true, true, true)]
    // A cold resume of a conversation is left to the menu by the tile itself, and can still be done.
    [InlineData(true, true, true, false)]
    // A tile with nothing to restart has neither.
    [InlineData(false, false, false, false)]
    public void Restart_is_a_header_button_only_where_the_tile_does_not_leave_it_to_the_menu(
        bool preferOverflow, bool offersRestart, bool canRestart, bool hasButton)
    {
        var leaf = Leaf(new RestartingTile(preferOverflow, offersRestart));

        Assert.Equal(canRestart, leaf.CanRestart);
        Assert.Equal(hasButton, leaf.RestartHasHeaderButton);
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
