using Avalonia.Input;

namespace mTiles.Services.Speech;

/// <summary>What a keystroke means to something that is listening for a new shortcut.</summary>
internal enum HotkeyCaptureAction
{
    /// <summary>Not part of an answer. The key must be left alone — <em>including</em> not being marked
    /// handled, which is the half that has already been got wrong once.</summary>
    Ignore,

    /// <summary>"No shortcut at all."</summary>
    Clear,

    /// <summary>A complete gesture to bind.</summary>
    Bind,
}

internal readonly record struct HotkeyCaptureResult(HotkeyCaptureAction Action, HotkeyGesture Gesture)
{
    /// <summary>Whether the keystroke was consumed, which is exactly when it may be marked handled.</summary>
    public bool Taken => Action != HotkeyCaptureAction.Ignore;

    public static HotkeyCaptureResult Ignored => new(HotkeyCaptureAction.Ignore, default);
}

/// <summary>
/// Reading a shortcut off the keyboard, as a function rather than as a key handler.
/// </summary>
/// <remarks>
/// <para>These are rules, not plumbing, and until now they lived in <c>SettingsView.axaml.cs</c> where
/// nothing could test them: which keys are not an answer, which key means "none", and — the one that
/// matters most — that a keystroke is marked handled <em>only</em> where it is actually taken. Marking it
/// first, before the early exits, was the whole of a real bug: Escape was swallowed and the settings
/// dialog could not be closed.</para>
/// <para>Pulled out here because the setup wizard now reads a shortcut too. Two copies of this would be
/// two chances to reintroduce that, and they would drift apart at the first change to either.</para>
/// </remarks>
internal static class HotkeyCapture
{
    /// <summary>
    /// What to do about <paramref name="key"/> pressed with <paramref name="modifiers"/>.
    /// </summary>
    /// <remarks>
    /// <para>Some keys are let through untouched rather than recorded. A bare <b>modifier</b> is what
    /// every combination starts with, so acting on it would store "Alt" the moment somebody reached for
    /// Alt+Space. <b>Tab</b> is how you leave a field — swallowing it traps the keyboard. And
    /// <b>Escape</b> is how you leave the dialog; recorded, it would bind the key that cancels dictation
    /// to starting it, and swallowed, it would strand the user in something that will not close.</para>
    /// <para><b>Backspace</b> and <b>Delete</b> clear it — the convention every shortcut field follows,
    /// and the only way to say "no shortcut" from the keyboard now that there is no separate switch.
    /// Only unmodified: <c>Ctrl+Backspace</c> is a perfectly ordinary gesture to want, and reading it as
    /// "clear" would make one of the few chords nobody else uses impossible to bind.</para>
    /// </remarks>
    public static HotkeyCaptureResult Interpret(Key key, KeyModifiers modifiers)
    {
        // `IsBindable` covers the modifiers and `Key.None` — the latter being what the toolkit reports
        // for a keystroke it could not map, which would otherwise be bound as the keycap `None` and then
        // match every other unmappable key.
        if (!HotkeyGesture.IsBindable(key) || key is Key.Tab or Key.Escape)
            return HotkeyCaptureResult.Ignored;

        if (key is Key.Back or Key.Delete && modifiers == KeyModifiers.None)
            return new HotkeyCaptureResult(HotkeyCaptureAction.Clear, default);

        return new HotkeyCaptureResult(HotkeyCaptureAction.Bind, new HotkeyGesture(modifiers, key));
    }
}
