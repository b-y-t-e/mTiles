namespace mTiles.Services;

/// <summary>Whether a dialog is open, asked by everything that routes input at the application.</summary>
/// <remarks>
/// <para>What "modal" means here, and the reason it is a fact anyone can ask rather than something a
/// dialog keeps to itself. A dialog is drawn over a scrim, and a scrim stops the pointer and nothing
/// else — so before this, a question was modal only to the mouse: Tab walked into the controls behind
/// it, and Alt+Space dictated a sentence into the terminal tile underneath it. Both were reported
/// from a real session.</para>
/// <para>It lives in <c>Services</c> rather than beside <c>OverlayHost</c> because the two sides face
/// opposite ways: <c>Views.OverlayHost</c> is the only thing that ever sets it, and the readers are
/// input routing (<see cref="Speech.DictationTextSink"/>, and the phone's keys through it) which must
/// not reach into <c>Views</c>. One flag, one writer, and the arrow points the way the project's
/// layering already points.</para>
/// <para>A count rather than a boolean, because dialogs stack: Settings is one of them and asks
/// questions of its own. Static, which is a claim about this application and not a shortcut — there
/// is one main window, every dialog is drawn in it, and the readers are reached from places that hold
/// no reference to a window at all.</para>
/// </remarks>
public static class ModalScope
{
    private static int _open;
    private static int _shortcutOwners;

    /// <summary>Whether anything is being asked of the user right now.</summary>
    public static bool IsAnyOpen => Volatile.Read(ref _open) > 0;

    /// <summary>Whether an open dialog handles the dictation shortcut itself.</summary>
    /// <remarks>
    /// <para>The window-level handler is <em>tunnelling</em>, so it sees a key before anything inside
    /// the window does. That was harmless while every dialog was a window of its own — the main
    /// window never saw their keys at all — and became a bug the moment they became controls in it:
    /// the speech wizard's last step asks the user to hold the shortcut and say something, and the
    /// window-level handler took the keystroke first, started a recording owned by the terminal tile
    /// behind the wizard, and left the wizard waiting for a gesture it never received.</para>
    /// <para>So a surface that means to handle the shortcut says so, and the window-level handler
    /// stands down for it — completely, without marking the key handled, so the surface's own
    /// tunnelling handler gets it next. Settings deliberately does not claim it: dictating into a
    /// settings text box is a feature, and there the window-level handler is the one that should
    /// act.</para>
    /// </remarks>
    public static bool ShortcutIsSpokenFor => Volatile.Read(ref _shortcutOwners) > 0;

    /// <summary>Called by <c>OverlayHost</c> as a dialog opens; the returned handle closes it.</summary>
    /// <remarks>A handle rather than a matching <c>Leave</c>, so a dialog that is torn down by an
    /// unusual path — the window closing under it — cannot leave the application believing something
    /// is still being asked, with dictation refusing to reach a tile for the rest of the session.</remarks>
    /// <param name="ownsDictationShortcut">See <see cref="ShortcutIsSpokenFor"/>.</param>
    public static IDisposable Enter(bool ownsDictationShortcut = false)
    {
        Interlocked.Increment(ref _open);
        if (ownsDictationShortcut)
            Interlocked.Increment(ref _shortcutOwners);
        return new Handle(ownsDictationShortcut);
    }

    private sealed class Handle(bool ownsDictationShortcut) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Once. Disposing twice would take the count below what is actually open, and the first
            // symptom of that is a dialog that is modal to nothing.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            Interlocked.Decrement(ref _open);
            if (ownsDictationShortcut)
                Interlocked.Decrement(ref _shortcutOwners);
        }
    }
}
