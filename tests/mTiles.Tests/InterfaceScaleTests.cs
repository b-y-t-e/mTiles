using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>The interface scale is never a number that leaves the window empty.</summary>
/// <remarks>
/// Every one of these is reachable: <c>settings.json</c> is hand-editable, and it is also read by an
/// older build after a Velopack rollback. A scale of zero draws nothing at all, including the dialog
/// holding the mistake, so the guard is what makes the setting recoverable from inside the application.
/// </remarks>
public class InterfaceScaleTests
{
    [Theory]
    [InlineData(0, InterfaceScale.Default)]
    [InlineData(-1, InterfaceScale.Default)]
    [InlineData(double.NaN, InterfaceScale.Default)]
    [InlineData(double.PositiveInfinity, InterfaceScale.Default)]
    [InlineData(0.1, InterfaceScale.Min)]
    [InlineData(50, InterfaceScale.Max)]
    [InlineData(1.25, 1.25)]
    [InlineData(1, 1)]
    public void A_stored_scale_is_made_safe_to_draw_with(double stored, double expected) =>
        Assert.Equal(expected, InterfaceScale.Normalise(stored));

    /// <summary>Nothing is what an untouched installation gets.</summary>
    /// <remarks>The default is 1.0 and not the middle of the range: the compositor's own scale is
    /// already correct on most machines, and this is the adjustment on top of it.</remarks>
    [Fact]
    public void The_default_is_the_display_s_own_scale() =>
        Assert.Equal(1.0, InterfaceScale.Normalise(new mTiles.Models.AppSettings().UiScale));
}
