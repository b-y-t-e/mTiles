using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The context reading with the bar put away (<c>AppSettings.ShowContextBar</c> off): said in one word
/// rather than not at all, and the bar itself not drawn.
/// </summary>
public sealed class ContextReadingTests
{
    [Theory]
    [InlineData(42.4, 1000L, "42%")]
    [InlineData(99.6, null, "100%")]
    [InlineData(null, 128_000L, "128k")]
    [InlineData(null, null, "")]
    public void The_short_reading_is_the_percentage_then_the_tokens_then_nothing(
        double? percent, long? used, string expected) =>
        Assert.Equal(expected, ContextGaugeViewModel.ShortReadingOf(percent, used));

    [Fact]
    public void A_hidden_bar_is_not_drawn_and_still_carries_the_reading()
    {
        var gauge = new ContextGaugeViewModel { KeepsItsPlace = true, IsHidden = true };

        gauge.Show(used: 50_000, window: 200_000, cost: null);

        Assert.False(gauge.IsDrawn);
        Assert.Equal("25%", gauge.ShortReading);
    }

    [Fact]
    public void A_shown_bar_is_drawn_before_its_first_reading()
    {
        var gauge = new ContextGaugeViewModel { KeepsItsPlace = true };

        Assert.True(gauge.IsDrawn);
        Assert.Equal("", gauge.ShortReading);
    }

    [Fact]
    public void Clearing_the_reading_empties_the_short_form_too()
    {
        var gauge = new ContextGaugeViewModel { IsHidden = true };
        gauge.Show(used: 50_000, window: null, cost: null);

        gauge.Clear();

        Assert.Equal("", gauge.ShortReading);
    }
}
