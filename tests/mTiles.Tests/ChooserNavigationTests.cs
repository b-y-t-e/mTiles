using Avalonia;
using mTiles.Views;
using Xunit;
using static mTiles.Views.ChooserNavigation.Direction;

namespace mTiles.Tests;

public class ChooserNavigationTests
{
    // Two rows: three cards, then two - as a WrapPanel lays five cards out in a narrow tile.
    private static readonly IReadOnlyList<Rect> Cards =
    [
        new(0, 0, 100, 80), new(110, 0, 100, 80), new(220, 0, 100, 80),
        new(0, 90, 100, 80), new(110, 90, 100, 80),
    ];

    [Theory]
    [InlineData(0, Right, 1)]
    [InlineData(2, Right, 3)]  // reading order runs on into the next row
    [InlineData(4, Right, 4)]  // no wrap past the last card
    [InlineData(0, Left, 0)]
    [InlineData(3, Left, 2)]
    [InlineData(1, Down, 4)]   // straight below
    [InlineData(2, Down, 4)]   // nothing straight below: the nearest card of the next row
    [InlineData(4, Up, 1)]
    [InlineData(3, Down, 3)]   // last row: stays put
    [InlineData(0, Up, 0)]
    [InlineData(-1, Down, 0)]  // no highlight yet: any key starts at the first card
    public void Moves_by_position_not_by_index(int current, ChooserNavigation.Direction direction, int expected) =>
        Assert.Equal(expected, ChooserNavigation.Move(Cards, current, direction));

    [Fact]
    public void No_cards_is_no_answer() =>
        Assert.Equal(-1, ChooserNavigation.Move([], 0, Down));
}
