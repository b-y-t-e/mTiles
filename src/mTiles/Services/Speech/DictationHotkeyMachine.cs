using mTiles.Models;

namespace mTiles.Services.Speech;

/// <summary>
/// Turns key presses into "start recording" and "stop recording", with the awkward parts of
/// push-to-talk handled: auto-repeat, and a release the operating system reports twice.
/// </summary>
/// <remarks>
/// <para>A port of Handy's <c>transcription_coordinator.rs</c>. Two constants carry the behaviour:
/// a press within <see cref="DebounceMs"/> of the last one is the same press, and a release only
/// counts if no press follows it within <see cref="ReleaseGraceMs"/>. The second is the important one —
/// holding a key produces repeat events on some systems as a release/press burst, and without the grace
/// period a held key stops and restarts the recording several times a second.</para>
/// <para>No timers of its own: it schedules through a callback, so a test can run a whole hold-and-release
/// in no time at all and without a dispatcher.</para>
/// </remarks>
internal sealed class DictationHotkeyMachine
{
    public const int DebounceMs = 30;
    public const int ReleaseGraceMs = 50;

    /// <summary>
    /// How long a key may be believed to be held without a single repeat before the machine stops
    /// believing it.
    /// </summary>
    /// <remarks>
    /// Auto-repeat arrives tens of times a second, so any real hold refreshes this constantly. It exists
    /// for the release that never comes — the window losing focus mid-hold, a dialog stealing the key —
    /// which would otherwise leave the machine convinced the key is still down and every later press
    /// dismissed as repeat. A shortcut that goes permanently dead is a worse failure than one extra
    /// press being taken seriously.
    /// </remarks>
    public const int HeldWithoutRepeatMs = 1000;

    private readonly Func<DictationMode> _mode;
    private readonly Action _start;
    private readonly Action _stop;
    private readonly Func<DateTime> _clock;
    private readonly Action<TimeSpan, Action> _schedule;

    private DateTime _lastPress = DateTime.MinValue;
    private DateTime _lastKeyDown = DateTime.MinValue;
    private int _releaseGeneration;
    private bool _releasePending;

    /// <summary>Whether the key is believed to be physically down. Set by a press, cleared by a
    /// release — which is why the shortcut handler forwards releases in both modes, even though only
    /// push-to-talk acts on them.</summary>
    private bool _keyHeld;

    /// <param name="schedule">Runs an action after a delay — a dispatcher timer, in the application.</param>
    public DictationHotkeyMachine(Func<DictationMode> mode, Action start, Action stop,
        Func<DateTime>? clock = null, Action<TimeSpan, Action>? schedule = null)
    {
        _mode = mode;
        _start = start;
        _stop = stop;
        _clock = clock ?? (() => DateTime.UtcNow);
        _schedule = schedule ?? ((delay, action) =>
            Avalonia.Threading.DispatcherTimer.RunOnce(action, delay));
    }

    /// <summary>Whether this machine believes a recording is running.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>The gesture was pressed.</summary>
    public void KeyDown()
    {
        var now = _clock();
        var wasHeld = _keyHeld && now - _lastKeyDown < TimeSpan.FromMilliseconds(HeldWithoutRepeatMs);

        if (_releasePending)
        {
            // The release we were about to act on was auto-repeat. Keep recording.
            _releaseGeneration++;
            _releasePending = false;
            _lastPress = now;
            Held(now);
            return;
        }

        // A key that never came up is not a new press. Without this, holding the shortcut in toggle
        // mode ended the recording at the first auto-repeat — about half a second in, silently, and
        // with the user still speaking. Push-to-talk never noticed because its repeat branch did
        // nothing anyway; the two modes read the same event and only one of them was thought through.
        if (wasHeld)
        {
            _lastKeyDown = now;          // a hold that is still going on
            return;
        }

        if (IsRecording)
        {
            // Push-to-talk: the key is simply still held. Toggle: this is the second press.
            Held(now);
            if (_mode() == DictationMode.Toggle)
                StopNow();
            return;
        }

        // Debounced presses deliberately leave the held flag alone. They are not a hold — they are the
        // same press arriving twice — and marking the key down here would make the press that follows
        // look like auto-repeat and be dropped.
        if (now - _lastPress < TimeSpan.FromMilliseconds(DebounceMs))
            return;

        Held(now);
        _lastPress = now;
        IsRecording = true;
        _start();
    }

    /// <summary>The gesture, or one of its modifiers, was released.</summary>
    public void KeyUp()
    {
        // The key is up whatever the mode does about it: toggle acts on presses only, but it still has
        // to know that the next press is a press rather than the tail of a hold.
        _keyHeld = false;

        if (_mode() != DictationMode.PushToTalk || !IsRecording || _releasePending)
            return;

        var generation = ++_releaseGeneration;
        _releasePending = true;
        _schedule(TimeSpan.FromMilliseconds(ReleaseGraceMs), () =>
        {
            if (generation != _releaseGeneration || !IsRecording)
                return;

            _releasePending = false;
            StopNow();
        });
    }

    /// <summary>Something else ended the recording — Escape, a tile closing, dictation switched off.</summary>
    public void Reset()
    {
        _releaseGeneration++;
        _releasePending = false;
        IsRecording = false;
    }

    /// <summary>Records that the key is down as of <paramref name="now"/>.</summary>
    private void Held(DateTime now)
    {
        _keyHeld = true;
        _lastKeyDown = now;
    }

    private void StopNow()
    {
        IsRecording = false;
        _stop();
    }
}
