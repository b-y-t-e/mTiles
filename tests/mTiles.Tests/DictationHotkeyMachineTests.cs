using mTiles.Models;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Push-to-talk, driven without a keyboard, a dispatcher or a wall clock.
/// <para>The behaviour these pin down is the reason the machine exists at all: a held key does not
/// produce one press and one release. It produces a burst, and on some systems the burst includes
/// releases. Handy learned this the hard way (its issue #1539) and answered with a grace period; this
/// is the same answer, and these are the cases that tell whether it still works.</para>
/// </summary>
public class DictationHotkeyMachineTests
{
    /// <summary>
    /// The gate between a matched keystroke and acting on it, which is the half of the shortcut that
    /// has been got wrong in both directions: a key swallowed on a machine with no model, and a
    /// recording nobody could stop.
    /// </summary>
    [Theory]
    // recording, hotkey live, ready → may act
    [InlineData(false, true, true, true)]     // the ordinary case
    [InlineData(false, false, true, false)]   // dictation or the shortcut switched off, or rebinding
    [InlineData(false, true, false, false)]   // no model yet: the key belongs to the shell
    [InlineData(true, false, false, true)]    // ...but a recording it started is always stoppable
    [InlineData(true, true, true, true)]
    public void A_press_is_acted_on_when_the_shortcut_is_live_or_it_started_the_recording(
        bool recording, bool live, bool ready, bool expected)
        => Assert.Equal(expected, DictationHotkeys.MayActOnPress(recording, () => live, () => ready));

    /// <summary>
    /// Escape belongs to dictation only while the microphone is open, and only when nothing on screen
    /// has a better claim to it.
    /// </summary>
    /// <remarks>
    /// Both halves cost something when they are wrong, and in opposite directions: acting during
    /// transcription swallows a key vim wants for a recording that no longer exists, and acting while
    /// the settings dialog is open leaves that dialog on screen with no way to close it — the handler
    /// tunnels, so the dialog never sees the key at all.
    /// </remarks>
    [Theory]
    [InlineData(DictationState.Recording, false, true)]
    [InlineData(DictationState.Recording, true, false)]     // the settings overlay wants it
    [InlineData(DictationState.Transcribing, false, false)] // nothing left to abandon
    [InlineData(DictationState.Idle, false, false)]
    public void Escape_cancels_only_a_recording_nothing_else_is_waiting_on(
        DictationState state, bool spokenFor, bool expected)
        => Assert.Equal(expected, DictationHotkeys.EscapeCancels(state, spokenFor));

    /// <summary>
    /// A release is swallowed only in the mode that acts on one.
    /// </summary>
    /// <remarks>
    /// The machine is told about every release in both modes — that is how it tells auto-repeat from a
    /// second press — but only push-to-talk stops a recording with one. Marking them handled in toggle
    /// mode ate every space and alt release on its way to the terminal for as long as the recording
    /// lasted, and a terminal does see key-up events whenever the child asked for win32 input.
    /// </remarks>
    [Theory]
    [InlineData(true, DictationMode.PushToTalk, true, true)]
    [InlineData(true, DictationMode.Toggle, true, false)]
    [InlineData(false, DictationMode.PushToTalk, true, false)]   // nothing was recording: not ours to take
    [InlineData(false, DictationMode.Toggle, true, false)]
    // Letting go of Alt ends Alt+Space, and the machine is told — but the terminal saw that Alt go down
    // (a bare modifier never matches the gesture, so its key-down was not swallowed) and has to see it
    // come up. Swallowing it left a modifier stuck down in the child after an ordinary dictation.
    [InlineData(true, DictationMode.PushToTalk, false, false)]
    public void A_release_is_swallowed_only_where_it_does_something(
        bool wasRecording, DictationMode mode, bool isMainKey, bool expected)
        => Assert.Equal(expected, DictationHotkeys.ReleaseIsOurs(wasRecording, mode, isMainKey));

    /// <summary>
    /// Holding the shortcut in toggle mode keeps recording — auto-repeat is not a second press.
    /// </summary>
    /// <remarks>
    /// The operating system starts repeating a held key after about half a second, and every repeat
    /// arrives as a fresh key-down. Toggle mode read the second one as "press again to stop", so anybody
    /// who held the shortcut out of habit — the gesture the other mode teaches — got half a second of
    /// audio and then silence. Push-to-talk never noticed, because its repeat branch did nothing anyway:
    /// one event, two modes, and only one of them thought through.
    /// </remarks>
    [Fact]
    public void In_toggle_mode_a_held_key_does_not_stop_its_own_recording()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = 0;
        var stops = 0;
        var machine = new DictationHotkeyMachine(() => DictationMode.Toggle,
            () => starts++, () => stops++, () => clock, (_, _) => { });

        machine.KeyDown();                      // the press
        Assert.Equal(1, starts);

        // Held: repeats at roughly thirty a second, for two seconds.
        for (var i = 0; i < 60; i++)
        {
            clock = clock.AddMilliseconds(33);
            machine.KeyDown();
        }

        Assert.Equal(0, stops);
        Assert.True(machine.IsRecording);

        // Letting go changes nothing in toggle mode; the next press is what stops it.
        clock = clock.AddMilliseconds(33);
        machine.KeyUp();
        Assert.Equal(0, stops);

        clock = clock.AddSeconds(3);
        machine.KeyDown();
        Assert.Equal(1, stops);
        Assert.False(machine.IsRecording);
    }

    /// <summary>
    /// A release that never arrives must not leave the shortcut dead.
    /// </summary>
    /// <remarks>
    /// The window can lose focus mid-hold, and then there is no key-up to clear the held flag. Repeats
    /// come tens of times a second, so a long gap since the last one means the hold is over, whatever
    /// the machine was told.
    /// </remarks>
    [Fact]
    public void A_press_long_after_the_last_repeat_counts_even_if_no_release_was_seen()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = 0;
        var machine = new DictationHotkeyMachine(() => DictationMode.Toggle,
            () => starts++, () => { }, () => clock, (_, _) => { });

        machine.KeyDown();
        machine.Reset();                        // the recording ended some other way

        clock = clock.AddSeconds(5);            // and the release was never delivered
        machine.KeyDown();

        Assert.Equal(2, starts);
    }

    /// <summary>
    /// Whether a model is on disk is a question about the filesystem, and this runs for every key the
    /// window sees — including every character typed into a terminal. It is asked last, or not at all.
    /// </summary>
    [Fact]
    public void Whether_a_model_is_ready_is_not_asked_unless_it_matters()
    {
        var asked = 0;
        bool Ready() { asked++; return true; }

        DictationHotkeys.MayActOnPress(recording: true, () => true, Ready);
        Assert.Equal(0, asked);

        DictationHotkeys.MayActOnPress(recording: false, () => false, Ready);
        Assert.Equal(0, asked);

        DictationHotkeys.MayActOnPress(recording: false, () => true, Ready);
        Assert.Equal(1, asked);
    }

    private sealed class Harness
    {
        private readonly List<(TimeSpan Delay, Action Action)> _scheduled = [];

        public DictationMode Mode { get; set; } = DictationMode.PushToTalk;
        public DateTime Now { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public int Starts { get; private set; }
        public int Stops { get; private set; }

        public DictationHotkeyMachine Machine { get; }

        public Harness()
        {
            Machine = new DictationHotkeyMachine(
                () => Mode,
                () => Starts++,
                () => Stops++,
                () => Now,
                (delay, action) => _scheduled.Add((delay, action)));
        }

        public void Advance(int milliseconds) => Now = Now.AddMilliseconds(milliseconds);

        /// <summary>Runs whatever was scheduled, as a timer eventually would.</summary>
        public void RunTimers()
        {
            var due = _scheduled.ToList();
            _scheduled.Clear();
            foreach (var (_, action) in due)
                action();
        }

        public TimeSpan LastDelay => _scheduled[^1].Delay;
    }

    [Fact]
    public void Holding_the_key_records_and_releasing_it_transcribes()
    {
        var h = new Harness();

        h.Machine.KeyDown();
        Assert.Equal(1, h.Starts);
        Assert.True(h.Machine.IsRecording);

        h.Machine.KeyUp();
        Assert.Equal(0, h.Stops);          // still inside the grace period
        Assert.Equal(TimeSpan.FromMilliseconds(DictationHotkeyMachine.ReleaseGraceMs), h.LastDelay);

        h.RunTimers();
        Assert.Equal(1, h.Stops);
        Assert.False(h.Machine.IsRecording);
    }

    [Fact]
    public void Auto_repeat_while_the_key_is_held_does_not_start_a_second_recording()
    {
        var h = new Harness();
        h.Machine.KeyDown();

        for (var i = 0; i < 20; i++)
        {
            h.Advance(33);
            h.Machine.KeyDown();
        }

        Assert.Equal(1, h.Starts);
        Assert.Equal(0, h.Stops);
    }

    /// <summary>
    /// The case the grace period exists for: a release immediately followed by a press is one key still
    /// being held, not two gestures. Without this the recording would stop and restart several times a
    /// second, and every fragment would be transcribed separately.
    /// </summary>
    [Fact]
    public void A_release_followed_at_once_by_a_press_is_still_one_recording()
    {
        var h = new Harness();
        h.Machine.KeyDown();

        h.Machine.KeyUp();
        h.Advance(10);
        h.Machine.KeyDown();
        h.RunTimers();                     // the release timer fires, but its generation is stale

        Assert.Equal(1, h.Starts);
        Assert.Equal(0, h.Stops);
        Assert.True(h.Machine.IsRecording);

        h.Machine.KeyUp();
        h.RunTimers();
        Assert.Equal(1, h.Stops);
    }

    [Fact]
    public void Two_presses_within_the_debounce_are_one_press()
    {
        var h = new Harness();

        h.Machine.KeyDown();
        h.Machine.KeyUp();
        h.RunTimers();
        Assert.Equal(1, h.Starts);

        h.Advance(DictationHotkeyMachine.DebounceMs - 1);
        h.Machine.KeyDown();
        Assert.Equal(1, h.Starts);

        h.Advance(2);
        h.Machine.KeyDown();
        Assert.Equal(2, h.Starts);
    }

    [Fact]
    public void In_toggle_mode_the_second_press_stops_and_releases_do_nothing()
    {
        var h = new Harness { Mode = DictationMode.Toggle };

        h.Machine.KeyDown();
        Assert.Equal(1, h.Starts);

        h.Machine.KeyUp();
        h.RunTimers();
        Assert.Equal(0, h.Stops);
        Assert.True(h.Machine.IsRecording);

        h.Advance(1000);
        h.Machine.KeyDown();
        Assert.Equal(1, h.Stops);
        Assert.False(h.Machine.IsRecording);
    }

    [Fact]
    public void A_reset_stops_a_pending_release_from_firing()
    {
        var h = new Harness();
        h.Machine.KeyDown();
        h.Machine.KeyUp();

        h.Machine.Reset();                 // Escape, or the tile closing
        h.RunTimers();

        Assert.Equal(0, h.Stops);
        Assert.False(h.Machine.IsRecording);
    }

    /// <summary>
    /// Something other than the shortcut can end a recording — the tile's microphone button, a failure,
    /// a tile closing. In toggle mode a machine that had not heard about it would spend the user's next
    /// press switching off something already stopped, and only the press after that would record.
    /// <see cref="DictationHotkeyMachine.Reset"/> is how the coordinator tells it, from the service's
    /// own state change.
    /// </summary>
    [Fact]
    public void In_toggle_mode_a_recording_stopped_elsewhere_frees_the_next_press_to_start()
    {
        var h = new Harness { Mode = DictationMode.Toggle };

        h.Machine.KeyDown();
        Assert.Equal(1, h.Starts);

        h.Machine.Reset();                 // the microphone button stopped it

        h.Advance(1000);
        h.Machine.KeyDown();
        Assert.Equal(2, h.Starts);
        Assert.Equal(0, h.Stops);
    }

    [Fact]
    public void A_release_without_a_recording_does_nothing()
    {
        var h = new Harness();
        h.Machine.KeyUp();
        h.RunTimers();

        Assert.Equal(0, h.Starts);
        Assert.Equal(0, h.Stops);
    }
}
