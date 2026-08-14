using Avalonia.Input;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Parsing what the user typed into the shortcut box. A gesture that cannot be parsed is a setting that
/// silently does nothing, which is why the parser refuses rather than guesses.
/// </summary>
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("alt + space")]
    [InlineData("Ctrl+Shift+D")]
    [InlineData("F5")]
    public void Recognisable_shortcuts_parse(string text)
        => Assert.True(HotkeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alt")]              // a modifier is not a shortcut
    [InlineData("Ctrl+Shift")]
    [InlineData("Alt+Space+D")]      // two keys
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Alt+LeftShift")]    // naming a modifier as the key
    // Enum.TryParse takes numbers as well as names, so these used to parse: one to a Key that is not a
    // key on any keyboard, one to a value the enum does not define at all. Either way the settings file
    // ends up holding a shortcut nobody can press, shown in the box as though it were configured.
    [InlineData("Alt+207")]
    [InlineData("Alt+9999")]
    [InlineData("Ctrl+-1")]
    public void Unusable_shortcuts_are_refused(string text)
        => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Parsing_keeps_the_modifiers_and_the_key()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+D", out var gesture));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, gesture.Modifiers);
        Assert.Equal(Key.D, gesture.Key);
    }

    [Fact]
    public void A_gesture_round_trips_through_its_own_text()
    {
        Assert.True(HotkeyGesture.TryParse("alt+space", out var parsed));
        Assert.Equal("Alt+Space", parsed.ToString());
        Assert.True(HotkeyGesture.TryParse(parsed.ToString(), out var again));
        Assert.Equal(parsed, again);
    }

    [Fact]
    public void A_press_matches_only_with_exactly_the_modifiers_named()
    {
        var gesture = new HotkeyGesture(KeyModifiers.Alt, Key.Space);

        Assert.True(gesture.MatchesPress(Key.Space, KeyModifiers.Alt));
        Assert.False(gesture.MatchesPress(Key.Space, KeyModifiers.None));
        Assert.False(gesture.MatchesPress(Key.Space, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.False(gesture.MatchesPress(Key.D, KeyModifiers.Alt));
    }

    /// <summary>
    /// Letting go of Alt ends Alt+Space just as surely as letting go of the space bar, and the two orders
    /// are equally likely. A release rule that only watched the main key would leave the microphone open
    /// whenever somebody released the modifier first.
    /// </summary>
    [Fact]
    public void Releasing_the_key_or_any_of_its_modifiers_ends_the_gesture()
    {
        var gesture = new HotkeyGesture(KeyModifiers.Alt, Key.Space);

        Assert.True(gesture.MatchesRelease(Key.Space));
        Assert.True(gesture.MatchesRelease(Key.LeftAlt));
        Assert.True(gesture.MatchesRelease(Key.RightAlt));
        Assert.False(gesture.MatchesRelease(Key.LeftCtrl));
        Assert.False(gesture.MatchesRelease(Key.A));
    }
}
