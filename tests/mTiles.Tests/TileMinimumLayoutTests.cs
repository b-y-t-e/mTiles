using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The pane a splitter may shrink is clamped by everything inside it, however deep.
/// </summary>
/// <remarks>
/// A minimum on the pane alone was measurably not enough: a star-sized pane never grows to what its
/// content needs, so a pane holding its own split was squeezed to one tile's width and laid its two
/// tiles out past its edge, under the opaque card next door — the tile disappeared exactly as it had
/// before there was any minimum at all.
/// </remarks>
public class TileMinimumLayoutTests
{
    private const double Gap = 8;

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileMinimumLayoutTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static LeafTileNodeViewModel Tile() =>
        new(TileContentType.Empty, null, "", new TileActivationScope());

    /// <summary>The tree shown in a window, with a first pane dragged as far left as it will go.</summary>
    private static (Grid Grid, Window Window) Show(SplitTileNodeViewModel root, double width = 400)
    {
        root.SplitRatio = 0.001;
        var view = new TileNodeView { DataContext = root };
        var window = new Window { Content = view, Width = width, Height = 300 };
        window.Show();
        return ((Grid)view.Content!, window);
    }

    [Fact]
    public void A_pane_holding_a_further_split_keeps_room_for_both_of_its_tiles()
    {
        OnUiThread(() =>
        {
            var inner = new SplitTileNodeViewModel(Orientation.Vertical, Tile(), Tile());
            var (grid, window) = Show(new SplitTileNodeViewModel(Orientation.Vertical, inner, Tile()));

            Assert.Equal(TileMinimumSize.Width(inner, Gap), grid.ColumnDefinitions[0].MinWidth);
            Assert.True(grid.ColumnDefinitions[0].ActualWidth >= TileMinimumSize.Width(inner, Gap),
                $"the nested pane was squeezed to {grid.ColumnDefinitions[0].ActualWidth}px");

            window.Close();
        });
    }

    [Fact]
    public void Splitting_a_tile_deep_inside_raises_the_minimum_of_the_panes_above_it()
    {
        OnUiThread(() =>
        {
            var inner = new SplitTileNodeViewModel(Orientation.Vertical, Tile(), Tile());
            var (grid, window) = Show(new SplitTileNodeViewModel(Orientation.Vertical, inner, Tile()));

            inner.First = new SplitTileNodeViewModel(Orientation.Vertical, Tile(), Tile());

            Assert.Equal(TileMinimumSize.Width(inner, Gap), grid.ColumnDefinitions[0].MinWidth);

            window.Close();
        });
    }

    /// <summary>
    /// A window narrower than the tiles in it want must still show them all, smaller.
    /// </summary>
    /// <remarks>
    /// Minimums are floors, not preferences: two of them adding up to more than the grid has spill the
    /// far pane past the edge, where it is clipped away entirely. Nothing needs to touch a tile
    /// splitter to get there — the window has no minimum size of its own.
    /// </remarks>
    [Fact]
    public void A_grid_with_less_room_than_the_minimums_want_still_holds_both_panes()
    {
        OnUiThread(() =>
        {
            var inner = new SplitTileNodeViewModel(Orientation.Vertical, Tile(), Tile());
            var root = new SplitTileNodeViewModel(Orientation.Vertical, inner, Tile());
            var (grid, window) = Show(root, width: 120);

            var wanted = TileMinimumSize.Width(root, Gap);
            Assert.True(grid.Bounds.Width < wanted, "the window was not narrow enough to test this");

            var used = grid.ColumnDefinitions[0].ActualWidth + Gap + grid.ColumnDefinitions[2].ActualWidth;
            Assert.True(used <= grid.Bounds.Width + 0.5, $"the panes took {used}px of {grid.Bounds.Width}px");
            Assert.True(grid.ColumnDefinitions[2].ActualWidth > 0, "the second pane was pushed off the grid");

            window.Close();
        });
    }
}
