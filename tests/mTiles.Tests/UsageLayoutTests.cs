using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which way round the usage tile lays its cards out.
/// </summary>
/// <remarks>
/// The rule is an opinion about the drawing, so it is argued here rather than inside a code-behind
/// nothing can reach. What it is protecting is the one promise the tile's design makes about a narrow
/// tile — the picture goes and every figure stays — which a shared line cannot keep once there are more
/// windows on it than the widest figure has room for.
/// </remarks>
public class UsageLayoutTests
{
    [Theory]
    // A tile across the top of a workspace: room for four windows and a balance.
    [InlineData(700.0, 5, true, 2, false)]
    // The same account in a column beside a terminal, which is where the figures were being cut.
    [InlineData(180.0, 2, true, 2, true)]
    // Two windows fit in a column an account with four cannot use.
    [InlineData(300.0, 2, true, 2, false)]
    [InlineData(300.0, 4, true, 2, true)]
    // Nothing on the tile draws a bar, so nothing on it needs the width a bar does: the three windows
    // and the balance that stack a subscription still share a line here.
    [InlineData(380.0, 4, false, 3, false)]
    [InlineData(380.0, 4, true, 2, true)]
    // agy's four windows, whose labels carry the model family as well ("Claude and GPT 7d"). At a width
    // where four two-character labels share a line comfortably, these cannot: the label eats what the
    // figure needs, and the figure is the part that gets clipped.
    [InlineData(700.0, 4, true, 17, true)]
    // Given the room those labels actually ask for, the same four go back to sharing a line.
    [InlineData(950.0, 4, true, 17, false)]
    public void The_shape_follows_what_has_to_fit(
        double width, int items, bool bars, int longestLabel, bool expectedVertical) =>
        Assert.Equal(expectedVertical, UsageLayout.IsVerticalFor(width, items, bars, longestLabel));

    /// <summary>A label no longer than the width was written for costs nothing.</summary>
    /// <remarks>The bar is what gives way as a row narrows, so a short label buys no width back - it
    /// only means the bar keeps more of it. Anything longer costs the characters it is drawn in.
    /// </remarks>
    [Theory]
    [InlineData(true, 0, 130.0)]
    [InlineData(true, 2, 130.0)]
    [InlineData(true, 9, 172.0)]
    [InlineData(false, 3, 90.0)]
    [InlineData(false, 8, 120.0)]
    public void A_longer_label_costs_what_it_is_drawn_in(
        bool bars, int longestLabel, double expected) =>
        Assert.Equal(expected, UsageLayout.WindowMinWidthFor(bars, longestLabel));

    /// <summary>A control that has not been laid out yet is not a narrow one.</summary>
    /// <remarks>A tile that starts stacked and springs sideways on its first layout pass is worse than
    /// one that never moves, so an unmeasured width - and an account with nothing to lay out - keeps the
    /// shape the tile was designed in.</remarks>
    [Theory]
    [InlineData(0.0, 4)]
    [InlineData(-1.0, 4)]
    [InlineData(120.0, 0)]
    public void Nothing_measured_is_not_narrow(double width, int items) =>
        Assert.False(UsageLayout.IsVerticalFor(width, items, hasBarWindows: true,
            longestLabelLength: 2));
}
