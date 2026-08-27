using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The three shapes the elapsed label switches between, and where it switches.
/// </summary>
/// <remarks>
/// A table because the boundaries are the whole of it: 59 seconds and 60 seconds are written in two
/// different notations, and so are 59:59 and 1:00:00. Each of those is one comparison away from being
/// off by a second in a label the user reads while waiting.
/// </remarks>
public class ElapsedDisplayTests
{
    [Theory]
    // Under a minute: seconds with their unit, because "0:07" reads as a duration nobody measured.
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(42, "42s")]
    [InlineData(59, "59s")]
    // Minutes: a colon says the unit, and the seconds are padded so the field stops changing width.
    [InlineData(60, "1:00")]
    [InlineData(247, "4:07")]
    [InlineData(3599, "59:59")]
    // Hours: the minutes gain their padding as they become the middle field.
    [InlineData(3600, "1:00:00")]
    [InlineData(3753, "1:02:33")]
    [InlineData(45296, "12:34:56")]
    public void Writes_the_span_in_the_shortest_form_that_says_it(int seconds, string expected)
        => Assert.Equal(expected, ElapsedDisplay.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Truncates_rather_than_rounds()
    {
        // A label that says "1s" the instant a run starts is claiming a second that has not happened.
        Assert.Equal("0s", ElapsedDisplay.Format(TimeSpan.FromMilliseconds(999)));
        Assert.Equal("59s", ElapsedDisplay.Format(TimeSpan.FromMilliseconds(59_999)));
    }

    [Fact]
    public void A_negative_span_is_answered_not_thrown()
    {
        // Unreachable through the Stopwatch the tile measures with, and still not worth an exception on
        // the UI thread if some other caller ever hands one over.
        Assert.Equal("0s", ElapsedDisplay.Format(TimeSpan.FromSeconds(-5)));
    }
}
