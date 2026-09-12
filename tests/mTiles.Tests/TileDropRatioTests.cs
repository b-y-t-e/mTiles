using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a tile dropped between others takes, and what the tiles already there are left with.
/// </summary>
/// <remarks>The rule is two lines of arithmetic producing two ratios that are not the shares anybody
/// reasons about, so the assertions are written in the shares instead: the newcomer gets a third, and
/// the pair it landed between keep their proportion to each other. Either of those coming out wrong is
/// a layout that reads as a nested pane rather than as a row of three.</remarks>
public class TileDropRatioTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.2)]
    [InlineData(0.35)]
    [InlineData(0.95)]
    public void A_tile_dropped_on_a_gutter_takes_a_third(double ratio)
    {
        var (first, newcomer, second) = SharesAfterGutterDrop(ratio);

        Assert.Equal(TileDropRatio.NewcomerShare, newcomer, precision: 9);
        Assert.Equal(1.0, first + newcomer + second, precision: 9);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.2)]
    [InlineData(0.35)]
    [InlineData(0.95)]
    public void The_two_tiles_it_landed_between_keep_their_proportion(double ratio)
    {
        var (first, _, second) = SharesAfterGutterDrop(ratio);

        Assert.Equal(ratio / (1 - ratio), first / second, precision: 9);
    }

    /// <summary>The hint is the room the drop will actually give, not a marker sized by eye.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.2)]
    public void The_gutter_hint_is_where_the_tile_lands(double ratio)
    {
        var (first, _, _) = SharesAfterGutterDrop(ratio);
        var (start, size) = TileDropRatio.GutterBand(ratio);

        Assert.Equal(first, start, precision: 9);
        Assert.Equal(TileDropRatio.NewcomerShare, size, precision: 9);
    }

    /// <summary>A ratio no layout can produce still answers with two usable numbers.</summary>
    /// <remarks>The stored ratio comes out of a splitter drag and a JSON file, and a division by zero
    /// here would be an exception raised while a drop is in flight, with the source already detached
    /// from the tree.</remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-4)]
    [InlineData(17)]
    public void A_degenerate_ratio_is_still_answered(double ratio)
    {
        var (outer, inner) = TileDropRatio.Gutter(ratio);

        Assert.InRange(outer, 0, 1);
        Assert.InRange(inner, 0, 1);
    }

    [Fact]
    public void A_tile_dropped_on_the_workspace_edge_takes_a_third_of_it()
    {
        Assert.Equal(TileDropRatio.NewcomerShare, TileDropRatio.Edge(newcomerFirst: true), precision: 9);
        Assert.Equal(1 - TileDropRatio.NewcomerShare, TileDropRatio.Edge(newcomerFirst: false), precision: 9);

        Assert.Equal((0, TileDropRatio.NewcomerShare), TileDropRatio.EdgeBand(newcomerFirst: true));
        Assert.Equal(
            (1 - TileDropRatio.NewcomerShare, TileDropRatio.NewcomerShare),
            TileDropRatio.EdgeBand(newcomerFirst: false));
    }

    /// <summary>The three shares the two ratios come out as, which is what the user sees.</summary>
    private static (double First, double Newcomer, double Second) SharesAfterGutterDrop(double ratio)
    {
        var (outer, inner) = TileDropRatio.Gutter(ratio);
        return (outer, (1 - outer) * inner, (1 - outer) * (1 - inner));
    }
}
