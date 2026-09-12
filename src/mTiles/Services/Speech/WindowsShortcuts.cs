using Avalonia.Input;

namespace mTiles.Services.Speech;

/// <summary>
/// The shortcuts Windows keeps for itself, as a table.
/// </summary>
/// <remarks>
/// <para><b>Written down rather than asked, because there is nobody to ask.</b> Every Linux desktop here
/// publishes a register of its shortcuts — <see cref="DesktopShortcuts"/> reads three of them — and
/// Windows publishes none: what the shell and the window manager have taken is documented and stable,
/// not enumerable. The choice is therefore between a table and saying nothing, and saying nothing is
/// what leaves a user holding a shortcut at an application that will never see it.</para>
/// <para><b>What belongs here is only what the window never sees.</b> The test is not "Windows does
/// something when you press this", it is "does the key-down reach the window at all" — because that is
/// the whole of what this sentence claims. <c>Alt+Tab</c>, <c>Alt+Esc</c>, <c>Ctrl+Esc</c>,
/// <c>Ctrl+Alt+Del</c> and the <c>Win</c> chords are taken by the shell before any application is told.
/// A gesture the window handles through <c>DefWindowProc</c> is the opposite case and is deliberately
/// <b>absent</b>: <c>Alt+Space</c> arrives as <c>WM_SYSKEYDOWN</c>, Avalonia raises a <c>KeyDown</c> from
/// it and <see cref="DictationHotkeys"/> sees it — measured, and the same measurement that says sending
/// <c>Alt+Space</c> to this window opens no window menu. So does <c>Alt+F4</c>, and so does
/// <c>Ctrl+Shift+Esc</c>. Listing them told somebody their working shortcut was dead, which is the one
/// direction of error the paragraph below exists to avoid.</para>
/// <para><b>Deliberately short, and deliberately not the whole Start-key space.</b> Windows assigns most
/// <c>Win</c> combinations, but not all, and a sentence that says a shortcut will never arrive has to be
/// right — a blanket rule would be wrong about the few that are free, and being wrong in that direction
/// tells somebody their working shortcut is broken. The ones here are each a documented shortcut of the
/// shell's, and the three <c>Win</c> entries are the ones a person might plausibly choose for dictation:
/// Windows' own dictation among them.</para>
/// <para>What this cannot cover is anything the user's own software has grabbed — a launcher, a capture
/// tool, a conferencing client with a push-to-talk of its own. That is what the setup wizard's timeout
/// is still for, on every platform.</para>
/// </remarks>
internal static class WindowsShortcuts
{
    private static readonly (KeyModifiers Modifiers, Key Key, string Name)[] Table =
    [
        (KeyModifiers.Alt, Key.Tab, "The task switcher"),
        (KeyModifiers.Alt, Key.Escape, "Windows' own window cycling"),
        (KeyModifiers.Control, Key.Escape, "The Start menu"),
        (KeyModifiers.Control | KeyModifiers.Alt, Key.Delete, "The Windows security screen"),
        (KeyModifiers.Meta, Key.H, "Windows' own dictation"),
        (KeyModifiers.Meta, Key.V, "Clipboard history"),
        (KeyModifiers.Meta, Key.L, "Lock screen"),
    ];

    /// <summary>What holds <paramref name="gesture"/> on Windows, or null when nothing here says.</summary>
    /// <remarks>Null is "not in the table", which is emphatically not "free": the table is a short list
    /// of certainties, and everything outside it is a shortcut this has no opinion about.</remarks>
    public static ShortcutOwner? Owner(HotkeyGesture gesture)
    {
        foreach (var (modifiers, key, name) in Table)
        {
            if (modifiers == gesture.Modifiers && key == gesture.Key)
                return new ShortcutOwner(name, CanBeFreed: false);
        }

        return null;
    }
}
