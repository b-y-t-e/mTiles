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

    /// <summary>Whether anything is being asked of the user right now.</summary>
    public static bool IsAnyOpen => Volatile.Read(ref _open) > 0;

    /// <summary>Called by <c>OverlayHost</c> as a dialog opens; the returned handle closes it.</summary>
    /// <remarks>A handle rather than a matching <c>Leave</c>, so a dialog that is torn down by an
    /// unusual path — the window closing under it — cannot leave the application believing something
    /// is still being asked, with dictation refusing to reach a tile for the rest of the session.</remarks>
    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _open);
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Once. Disposing twice would take the count below what is actually open, and the first
            // symptom of that is a dialog that is modal to nothing.
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Interlocked.Decrement(ref _open);
        }
    }
}
