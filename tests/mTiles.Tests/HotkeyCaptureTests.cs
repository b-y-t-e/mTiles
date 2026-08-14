using Avalonia.Input;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Reading a shortcut off the keyboard: which keystrokes are an answer, and which must be left alone.
/// </summary>
/// <remarks>
/// These rules used to live in <c>SettingsView.axaml.cs</c>, where nothing could reach them without a
/// window and a focused control — so the one that had already been got wrong in production, marking a
/// keystroke handled before deciding whether it was taken, had no test at all. Two screens read a
/// shortcut now; this is the one place that decides what a key press means.
/// </remarks>
public class HotkeyCaptureTests
{
    /// <summary>
    /// A bare modifier is not an answer, and neither are the two keys used to get out.
    /// </summary>
    /// <remarks>
    /// Every combination starts with a modifier, so binding one would store "Alt" the instant somebody
    /// reached for Alt+Space. Tab is how you leave a field; Escape is how you leave a dialog, and binding
    /// it would give the key that cancels dictation the job of starting it.
    /// </remarks>
    [Theory]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Escape)]
    [InlineData(Key.None)]
    public void Some_keys_are_not_an_answer(Key key)
    {
        var result = HotkeyCapture.Interpret(key, KeyModifiers.None);

        Assert.Equal(HotkeyCaptureAction.Ignore, result.Action);

        // The half that was the actual bug: not taken means not consumed, so whatever was going to
        // happen to the key still happens. Escape closed nothing for as long as this was the other way
        // round.
        Assert.False(result.Taken);
    }

    /// <summary>Escape stays available even while a modifier is held — the way out must not depend on
    /// which keys the user happens to be resting a finger on.</summary>
    [Fact]
    public void Escape_is_left_alone_whatever_is_held_with_it()
        => Assert.Equal(HotkeyCaptureAction.Ignore,
            HotkeyCapture.Interpret(Key.Escape, KeyModifiers.Control | KeyModifiers.Shift).Action);

    [Theory]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public void Backspace_and_delete_mean_no_shortcut(Key key)
    {
        var result = HotkeyCapture.Interpret(key, KeyModifiers.None);

        Assert.Equal(HotkeyCaptureAction.Clear, result.Action);
        Assert.True(result.Taken);
    }

    /// <summary>
    /// Held with a modifier they are an ordinary gesture, not the word "none".
    /// </summary>
    /// <remarks>
    /// The discriminating half of the pair above: without the unmodified check, <c>Ctrl+Backspace</c> —
    /// one of the few chords nothing else claims, and a reasonable thing to want — would be impossible
    /// to bind, and would silently switch the shortcut off instead.
    /// </remarks>
    [Theory]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public void With_a_modifier_they_are_a_shortcut_like_any_other(Key key)
    {
        var result = HotkeyCapture.Interpret(key, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureAction.Bind, result.Action);
        Assert.Equal(new HotkeyGesture(KeyModifiers.Control, key), result.Gesture);
    }

    [Fact]
    public void An_ordinary_combination_is_bound_as_pressed()
    {
        var result = HotkeyCapture.Interpret(Key.Space, KeyModifiers.Alt);

        Assert.Equal(HotkeyCaptureAction.Bind, result.Action);
        Assert.True(result.Taken);
        Assert.Equal("Alt+Space", result.Gesture.ToString());
    }

    /// <summary>A key with no modifier is bindable — it is a choice with a cost, not a mistake.</summary>
    [Fact]
    public void A_bare_key_is_allowed_and_said_out_loud()
    {
        var result = HotkeyCapture.Interpret(Key.F13, KeyModifiers.None);

        Assert.Equal(HotkeyCaptureAction.Bind, result.Action);
        Assert.NotNull(HotkeyAdvice.For(result.Gesture));
    }

    /// <summary>And the advice is about the missing modifier, not about everything.</summary>
    [Fact]
    public void A_combination_with_a_modifier_needs_no_warning()
        => Assert.Null(HotkeyAdvice.For(new HotkeyGesture(KeyModifiers.Alt, Key.Space)));

    /// <summary>
    /// The same advice about a shortcut as it is stored, which is how one arrives from a file or from
    /// another window.
    /// </summary>
    /// <remarks>
    /// The Speech tab used to work this out only in the property setter, which runs when somebody types
    /// in the box — not when the settings are loaded and not when the setup wizard closes, both of which
    /// write the backing field so as not to save everything back. So a bare key chosen in the wizard was
    /// accepted in silence, a warning from before it stayed up afterwards, and a shortcut the application
    /// cannot listen for opened the tab with nothing said about why the feature was dead.
    /// </remarks>
    [Theory]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("Alt+Space", null)]
    public void A_stored_shortcut_that_is_fine_is_left_without_comment(string? stored, string? expected)
        => Assert.Equal(expected, HotkeyAdvice.ForSetting(stored));

    [Fact]
    public void A_stored_bare_key_gets_the_same_sentence_as_one_just_pressed()
        => Assert.Equal(HotkeyAdvice.For(new HotkeyGesture(KeyModifiers.None, Key.F13)),
            HotkeyAdvice.ForSetting("F13"));

    /// <summary>
    /// <c>Key.None</c> names no key, and is refused at both ends.
    /// </summary>
    /// <remarks>
    /// It is what the toolkit reports for a keystroke it could not map — a dead key, some IME output — and
    /// it is a <em>defined</em> enum member, so every check asking only "is this a real Key" waves it
    /// through. Bound, it shows as the keycap <c>None</c>, stores as <c>Alt+None</c>, and then matches
    /// whatever other unmappable key is pressed next. The parser already refuses numbers for exactly this
    /// reason; the capture was the same hole one step along.
    /// </remarks>
    [Fact]
    public void A_key_that_names_nothing_is_refused_at_both_ends()
    {
        Assert.False(HotkeyCapture.Interpret(Key.None, KeyModifiers.Alt).Taken);
        Assert.False(HotkeyGesture.TryParse("Alt+None", out _));
        Assert.False(HotkeyGesture.IsBindable(Key.None));

        // And a real key still is bindable, or the guard could be "refuse everything".
        Assert.True(HotkeyGesture.IsBindable(Key.Space));
    }

    /// <summary>A setting naming something unusable says so, rather than leaving a dead feature
    /// unexplained.</summary>
    [Theory]
    [InlineData("Alt+9999")]
    [InlineData("Alt+None")]
    [InlineData("Ctrl")]
    [InlineData("nonsense")]
    public void A_stored_shortcut_that_cannot_be_listened_for_says_so(string stored)
        => Assert.Equal(HotkeyAdvice.Unparseable, HotkeyAdvice.ForSetting(stored));

    /// <summary>
    /// The keys of a shortcut, in the order they are written.
    /// </summary>
    /// <remarks>
    /// The wizard draws one chip per key, and the text form is built from the same list — so a rename
    /// here cannot make the chips and the stored setting disagree about what a modifier is called.
    /// </remarks>
    [Fact]
    public void A_gesture_lists_its_keys_in_the_order_it_writes_them()
    {
        var gesture = new HotkeyGesture(KeyModifiers.Control | KeyModifiers.Shift, Key.D);

        Assert.Equal(["Ctrl", "Shift", "D"], gesture.GetParts());
        Assert.Equal(string.Join('+', gesture.GetParts()), gesture.ToString());
    }
}
