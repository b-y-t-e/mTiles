using Avalonia.Input;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// One shortcut in three desktops' languages, as a table.
/// </summary>
/// <remarks>
/// Every number and every spelling here belongs to somebody else, which is the same reason
/// <c>AiAgentTests</c> pins the agents' flags: they move when their owners move them, and a wrong one
/// fails by naming the wrong culprit or none — neither of which shows up as an error anywhere. The two
/// Qt codes marked measured were read off a running Plasma 6 on 2026-09-12, out of KRunner's own answer.
/// </remarks>
public class ShortcutSpellingTests
{
    [Fact]
    public void A_gesture_is_one_Qt_key_code_with_the_modifier_bits_on_top()
    {
        (KeyModifiers Modifiers, Key Key, int? Expected, string Why)[] cases =
        [
            // Measured: KRunner answers for both of these, which is what pins the Alt bit, the plain
            // key range and the function-key base at once.
            (KeyModifiers.Alt, Key.Space, 0x08000020, "Alt+Space, KRunner's on a stock Plasma"),
            (KeyModifiers.Alt, Key.F2, 0x09000031, "Alt+F2, the same action's other shortcut"),
            (KeyModifiers.Control, Key.Space, 0x04000020, "Ctrl+Space"),
            (KeyModifiers.Meta, Key.Space, 0x10000020, "Meta+Space"),
            (KeyModifiers.Control | KeyModifiers.Shift, Key.D, 0x06000044, "two modifiers, or-ed"),
            (KeyModifiers.Control | KeyModifiers.Alt, Key.Space, 0x0C000020, "Ctrl+Alt+Space"),
            (KeyModifiers.None, Key.A, 0x41, "a bare letter is the letter"),
            (KeyModifiers.None, Key.D1, 0x31, "and a digit the digit"),
            (KeyModifiers.Alt, Key.Up, 0x09000013, "a named key"),
            // A key with no Qt spelling here is a question that does not get asked, rather than one
            // asked about the wrong key and answered "free" with confidence.
            (KeyModifiers.Alt, Key.MediaPlayPause, null, "a key nothing here can spell"),
        ];

        foreach (var (modifiers, key, expected, why) in cases)
            Assert.Equal(expected, ShortcutSpelling.QtKeyCode(new HotkeyGesture(modifiers, key)));
    }

    [Fact]
    public void GNOME_accelerators_are_compared_as_a_parsed_pair_and_not_as_strings()
    {
        (KeyModifiers Modifiers, Key Key, string Accelerator, bool Expected, string Why)[] cases =
        [
            // Read on a machine with the schema installed: GNOME's activate-window-menu is <Alt>space,
            // which is this application's own default shortcut.
            (KeyModifiers.Alt, Key.Space, "<Alt>space", true, "the window menu"),
            (KeyModifiers.Control | KeyModifiers.Alt, Key.T, "<Primary><Alt>t", true, "Primary is Control"),
            (KeyModifiers.Control | KeyModifiers.Alt, Key.T, "<Alt><Control>t", true, "order does not matter"),
            (KeyModifiers.Meta, Key.Space, "<Super>space", true, "Super is Meta"),
            (KeyModifiers.Meta, Key.Space, "<Mod4>space", true, "and so is Mod4"),
            (KeyModifiers.Alt, Key.F7, "<Alt>F7", true, "a function key keeps its case"),
            (KeyModifiers.Alt, Key.Space, "<Alt><Shift>space", false, "a modifier too many"),
            (KeyModifiers.Alt, Key.Space, "<Alt>Return", false, "a different key"),
            // A modifier this cannot read means a shortcut this cannot identify — never one it decides
            // is a match on the strength of the part it understood.
            (KeyModifiers.Alt, Key.Space, "<Hyper>space", false, "an unknown modifier"),
            (KeyModifiers.None, Key.Space, "space", true, "no modifiers at all"),
        ];

        foreach (var (modifiers, key, accelerator, expected, why) in cases)
            Assert.Equal(expected,
                ShortcutSpelling.MatchesGnomeAccelerator(new HotkeyGesture(modifiers, key), accelerator));
    }

    [Fact]
    public void A_Hyprland_bind_is_a_mod_mask_and_a_key_name()
    {
        (KeyModifiers Modifiers, Key Key, int Mask, string Name, bool Expected, string Why)[] cases =
        [
            (KeyModifiers.Alt, Key.Space, 8, "space", true, "ALT is 8"),
            (KeyModifiers.Meta, Key.Return, 64, "Return", true, "SUPER is 64"),
            (KeyModifiers.Control | KeyModifiers.Shift, Key.Q, 5, "q", true, "CTRL and SHIFT together"),
            (KeyModifiers.Alt, Key.Space, 0, "space", false, "the same key with no modifier"),
            (KeyModifiers.Alt, Key.Space, 8, "Return", false, "the same modifier with another key"),
        ];

        foreach (var (modifiers, key, mask, name, expected, why) in cases)
            Assert.Equal(expected,
                ShortcutSpelling.MatchesHyprlandBind(new HotkeyGesture(modifiers, key), mask, name));
    }

    /// <summary>
    /// The advice grows a sentence rather than replacing the one that was there.
    /// </summary>
    /// <remarks>Both can be true at once — a bare key is exactly the kind a launcher also takes — and
    /// the page has one slot for advice by design, so the two have to share it.</remarks>
    [Fact]
    public void An_owner_is_added_to_whatever_was_already_worth_saying()
    {
        var bare = new HotkeyGesture(KeyModifiers.None, Key.Space);
        var withModifier = new HotkeyGesture(KeyModifiers.Alt, Key.Space);

        var krunner = new ShortcutOwner("KRunner", CanBeFreed: true);

        Assert.Null(HotkeyAdvice.For(withModifier, null));
        Assert.Contains("KRunner", HotkeyAdvice.For(withModifier, krunner));

        var both = HotkeyAdvice.For(bare, krunner);
        Assert.Contains("Without a modifier", both);
        Assert.Contains("KRunner", both);
    }

    /// <summary>
    /// An owner that cannot be unbound is not offered as one that can.
    /// </summary>
    /// <remarks>The two sentences differ in the only part that is advice: a launcher's shortcut is a
    /// line in the user's own settings, and the Start menu on Windows is the shell's, so telling
    /// somebody to go and take it back there sends them looking for a screen that does not
    /// exist.</remarks>
    [Fact]
    public void What_cannot_be_taken_back_is_not_offered_as_something_to_take_back()
    {
        var freeable = HotkeyAdvice.TakenBy(new ShortcutOwner("KRunner", CanBeFreed: true));
        Assert.Contains("desktop settings", freeable);

        var fixed_ = HotkeyAdvice.TakenBy(new ShortcutOwner("The Start menu", CanBeFreed: false));
        Assert.DoesNotContain("desktop settings", fixed_);
        Assert.Contains("Choose another", fixed_);

        // And the table is what says which of the two a shortcut is on Windows.
        var owner = WindowsShortcuts.Owner(new HotkeyGesture(KeyModifiers.Control, Key.Escape));
        Assert.Equal("The Start menu", owner?.Name);
        Assert.False(owner?.CanBeFreed);

        // Everything outside the table is "no opinion", which is not the same as free.
        Assert.Null(WindowsShortcuts.Owner(new HotkeyGesture(
            KeyModifiers.Control | KeyModifiers.Alt, Key.Space)));
    }

    /// <summary>
    /// A gesture the window itself handles is not one the window never sees.
    /// </summary>
    /// <remarks>Windows routes <c>Alt+Space</c>, <c>Alt+F4</c> and <c>Ctrl+Shift+Esc</c> to the window
    /// as ordinary key messages and acts on them in <c>DefWindowProc</c>, so Avalonia raises a key-down
    /// and the dictation handler sees it — unlike <c>Alt+Tab</c> or <c>Ctrl+Esc</c>, which the shell
    /// takes first. The table listing them told a user that <c>Alt+Space</c> — the shortcut this
    /// application ships as its own default, and one measured to work here — was dead.</remarks>
    [Theory]
    [InlineData(KeyModifiers.Alt, Key.Space)]
    [InlineData(KeyModifiers.Alt, Key.F4)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, Key.Escape)]
    public void A_gesture_the_window_still_receives_is_not_in_the_table(KeyModifiers modifiers, Key key)
    {
        Assert.Null(WindowsShortcuts.Owner(new HotkeyGesture(modifiers, key)));
    }
}
