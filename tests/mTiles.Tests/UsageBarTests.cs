using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The arithmetic behind the bar: how many cells a width holds, how many the spending lights, and where
/// the clock's tick goes.
/// </summary>
/// <remarks>
/// Pulled out of <c>Render</c> and tested here because two of the three are opinions rather than
/// geometry — a little spending must not round away to an empty bar, and the tick must be drawn whether
/// or not the fill has passed it. The second is the correction: the tick used to be a differently
/// coloured cell, which could only exist where the fill had not reached, so it disappeared exactly when
/// an account began overspending.
/// </remarks>
public class UsageBarTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(4.0, 1)]
    [InlineData(5.9, 1)]
    [InlineData(9.9, 1)]
    [InlineData(10.0, 2)]
    [InlineData(100.0, 17)]
    public void A_width_holds_whole_cells_and_the_last_gap_is_not_one(double width, int cells) =>
        Assert.Equal(cells, UsageBar.CellsIn(width));

    /// <summary>Anything above nothing lights a cell; nothing lights none.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.4, 1)]
    [InlineData(3, 1)]
    [InlineData(50, 8)]
    [InlineData(100, 16)]
    public void Any_spending_at_all_lights_the_first_cell(double used, int filled) =>
        Assert.Equal(filled, UsageBar.FilledCells(used, cells: 16));

    /// <summary>A pace nobody could work out draws no tick, rather than one at the start.</summary>
    [Fact]
    public void No_pace_is_no_tick() => Assert.Null(UsageBar.MarkCell(null, cells: 16));

    /// <summary>The tick stays inside the bar at both ends.</summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(50.0, 8)]
    [InlineData(100.0, 15)]
    public void The_tick_is_a_cell_of_the_bar(double expected, int cell) =>
        Assert.Equal(cell, UsageBar.MarkCell(expected, cells: 16));

    /// <summary>It is drawn in the gap in front of its cell, so it covers no cell of its own.</summary>
    /// <remarks>Cells are six pixels apart — four of cell and two of gap — so the gap in front of cell
    /// <c>n</c> starts four pixels into the previous one's span.</remarks>
    [Theory]
    [InlineData(1, 4.0)]
    [InlineData(8, 46.0)]
    public void The_tick_sits_in_the_gap_before_its_cell(int cell, double offset) =>
        Assert.Equal(offset, UsageBar.MarkOffset(cell));

    /// <summary>The first cell has no gap in front of it, so its tick sits at the bar's own edge.</summary>
    [Fact]
    public void A_tick_at_the_start_stays_on_the_bar() => Assert.Equal(0.0, UsageBar.MarkOffset(0));
}
