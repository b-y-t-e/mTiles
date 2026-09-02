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
    [InlineData(700.0, 5, true, false)]
    // The same account in a column beside a terminal, which is where the figures were being cut.
    [InlineData(180.0, 2, true, true)]
    // Two windows fit in a column an account with four cannot use.
    [InlineData(300.0, 2, true, false)]
    [InlineData(300.0, 4, true, true)]
    // Nothing on the tile draws a bar, so nothing on it needs the width a bar does: the three windows
    // and the balance that stack a subscription still share a line here.
    [InlineData(380.0, 4, false, false)]
    [InlineData(380.0, 4, true, true)]
    public void The_shape_follows_what_has_to_fit(
        double width, int items, bool bars, bool expectedVertical) =>
        Assert.Equal(expectedVertical, UsageLayout.IsVerticalFor(width, items, bars));

    /// <summary>A control that has not been laid out yet is not a narrow one.</summary>
    /// <remarks>A tile that starts stacked and springs sideways on its first layout pass is worse than
    /// one that never moves, so an unmeasured width - and an account with nothing to lay out - keeps the
    /// shape the tile was designed in.</remarks>
    [Theory]
    [InlineData(0.0, 4)]
    [InlineData(-1.0, 4)]
    [InlineData(120.0, 0)]
    public void Nothing_measured_is_not_narrow(double width, int items) =>
        Assert.False(UsageLayout.IsVerticalFor(width, items, hasBarWindows: true));
}
