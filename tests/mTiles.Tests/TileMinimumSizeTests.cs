using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a pane may be shrunk to is a property of everything inside it, not of the pane.
/// </summary>
/// <remarks>
/// A minimum applied one level deep let the outer splitter squeeze a pane holding its own split down to
/// one tile's worth: the two tiles inside were then laid out past the pane's edge, under the opaque card
/// next door, so the tile the minimum was meant to protect vanished anyway.
/// </remarks>
public class TileMinimumSizeTests
{
    private const double Gap = 8;

    private static LeafTileNodeViewModel Tile() =>
        new(TileKindIds.Note, null, "", new TileActivationScope());

    private static SplitTileNodeViewModel Split(Orientation orientation,
        TileNodeViewModel first, TileNodeViewModel second) => new(orientation, first, second);

    [Fact]
    public void A_single_tile_asks_for_one_tile_on_each_axis()
    {
        var tile = Tile();

        Assert.Equal(TileMinimumSize.LeafSize, TileMinimumSize.Width(tile, Gap));
        Assert.Equal(TileMinimumSize.LeafSize, TileMinimumSize.Height(tile, Gap));
    }

    [Fact]
    public void A_split_spends_a_gutter_on_the_axis_it_divides_and_nothing_on_the_other()
    {
        var sideBySide = Split(Orientation.Vertical, Tile(), Tile());

        Assert.Equal(50 + Gap + 50, TileMinimumSize.Width(sideBySide, Gap));
        Assert.Equal(50, TileMinimumSize.Height(sideBySide, Gap));
    }

    [Fact]
    public void A_nested_split_raises_the_minimum_of_the_pane_holding_it()
    {
        var inner = Split(Orientation.Vertical, Tile(), Tile());
        var outer = Split(Orientation.Vertical, inner, Tile());

        Assert.Equal(50 + Gap + 50 + Gap + 50, TileMinimumSize.Width(outer, Gap));
    }

    [Fact]
    public void Minimums_that_fit_are_left_alone()
    {
        Assert.Equal((50d, 108d), TileMinimumSize.Fit(50, 108, available: 200));
    }

    [Fact]
    public void Minimums_that_do_not_fit_share_what_there_is_in_proportion()
    {
        var (first, second) = TileMinimumSize.Fit(50, 150, available: 100);

        Assert.Equal(25, first, precision: 6);
        Assert.Equal(75, second, precision: 6);
    }

    [Fact]
    public void A_size_that_is_not_known_yet_does_not_shrink_anything()
    {
        Assert.Equal((50d, 150d), TileMinimumSize.Fit(50, 150, available: 0));
    }

    [Fact]
    public void A_split_across_the_axis_takes_the_larger_of_its_two_sides()
    {
        var stacked = Split(Orientation.Horizontal, Tile(), Tile());
        var outer = Split(Orientation.Vertical, stacked, Tile());

        Assert.Equal(50 + Gap + 50, TileMinimumSize.Width(outer, Gap));
        Assert.Equal(50 + Gap + 50, TileMinimumSize.Height(outer, Gap));
    }
}
