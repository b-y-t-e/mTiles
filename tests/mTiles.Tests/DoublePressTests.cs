using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>Escape twice in a composer: what counts as the pair that empties the box.</summary>
public class DoublePressTests
{
    private static readonly DateTime T0 = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void One_press_is_not_a_pair() =>
        Assert.False(new DoublePress(DoublePress.Window).Press(T0));

    [Fact]
    public void Two_presses_inside_the_window_are()
    {
        var press = new DoublePress(DoublePress.Window);
        press.Press(T0);
        Assert.True(press.Press(T0.AddMilliseconds(300)));
    }

    [Fact]
    public void Two_presses_too_far_apart_are_not()
    {
        var press = new DoublePress(DoublePress.Window);
        press.Press(T0);
        Assert.False(press.Press(T0.AddMilliseconds(900)));
    }

    [Fact]
    public void A_third_press_starts_a_new_pair()
    {
        var press = new DoublePress(DoublePress.Window);
        press.Press(T0);
        press.Press(T0.AddMilliseconds(100));
        Assert.False(press.Press(T0.AddMilliseconds(200)));
    }

    [Fact]
    public void A_late_second_press_is_the_first_of_the_next_pair()
    {
        var press = new DoublePress(DoublePress.Window);
        press.Press(T0);
        press.Press(T0.AddSeconds(2));
        Assert.True(press.Press(T0.AddSeconds(2).AddMilliseconds(200)));
    }
}
