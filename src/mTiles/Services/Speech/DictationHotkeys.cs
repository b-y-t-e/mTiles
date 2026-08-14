using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Speech;

/// <summary>
/// The push-to-talk shortcut, listened for once at the window.
/// </summary>
/// <remarks>
/// <para>Tunnelling, and with <c>handledEventsToo</c>, for the same reason the clipboard coordinator
/// tunnels: the terminal consumes keys, so a bubbling handler never sees them. Key <em>up</em> also
/// rules out Avalonia's <c>KeyBindings</c>, which only fire on key down — and holding a key is the whole
/// gesture here.</para>
/// <para>The tile is resolved when the key goes down, not when the transcript arrives: dictation takes
/// seconds, and the text belongs to the tile the user was looking at when they spoke.</para>
/// </remarks>
internal static class DictationHotkeys
{
    private static Window? _window;
    private static DictationService? _service;
    private static SettingsService? _settings;
    private static Func<LeafTileNodeViewModel?>? _activeTile;
    private static DictationHotkeyMachine? _machine;

    /// <summary>
    /// Set while the user is typing a new shortcut into Settings.
    /// </summary>
    /// <remarks>
    /// Without it the feature cannot be reconfigured at all: this handler tunnels, so it sees the
    /// keystroke before the box does, matches the shortcut it is being asked to replace, marks the event
    /// handled — and starts recording instead of rebinding. The transcript then arrives in a terminal
    /// behind the modal. Nothing else can stand down for it, because dictating into a text box is a
    /// feature: the exemption has to be this one box, and it has to be explicit.
    /// </remarks>
    public static bool IsRebinding { get; private set; }

    /// <summary>
    /// Stands the shortcut down until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// <para>A scope rather than a settable flag, because the flag is only ever right if every path that
    /// raises it also lowers it, and there are three of those here: the box losing focus, the settings
    /// dialog being hidden, and the view leaving the tree. Two of them exist precisely because the first
    /// one does not always happen. With an assignment, each is a chance to write the wrong value or to
    /// write none; with a scope, they are all the same call, and disposing one twice — or disposing one
    /// that a later focus has already replaced — does nothing.</para>
    /// <para>Deliberately not a counter. Only one box in the application rebinds, and a count that leaks
    /// a single increment leaves dictation off with nothing on screen to explain it — exactly the
    /// failure this shape is meant to make impossible.</para>
    /// </remarks>
    public static IDisposable BeginRebinding()
    {
        IsRebinding = true;
        return new RebindScope();
    }

    private sealed class RebindScope : IDisposable
    {
        public void Dispose() => IsRebinding = false;
    }

    /// <summary>
    /// Whether something on screen has a stronger claim to Escape than a recording does.
    /// </summary>
    /// <remarks>
    /// The settings dialog is an overlay in this window rather than a window of its own, so it never
    /// gets the key: this handler tunnels, cancels the recording and marks Escape handled, and the
    /// dialog stays open. Dictating into a settings box is a feature, which makes that combination
    /// reachable by ordinary use — and of the two things Escape could mean there, closing the dialog is
    /// the one the user cannot do any other way. The recording can still be ended with the shortcut.
    /// </remarks>
    private static Func<bool>? _escapeSpokenFor;

    public static void Attach(Window window, DictationService service, SettingsService settings,
        Func<LeafTileNodeViewModel?> activeTile, Func<bool>? escapeSpokenFor = null)
    {
        Detach();

        _window = window;
        _service = service;
        _settings = settings;
        _activeTile = activeTile;
        _escapeSpokenFor = escapeSpokenFor;
        _machine = new DictationHotkeyMachine(() => settings.Settings.Speech.Mode, StartRecording, StopRecording);

        // Whoever else ends a recording — the tile's microphone button, an error, a tile closing — the
        // machine has to hear about it, or in toggle mode its next press spends itself switching off
        // something that already stopped.
        service.StateChanged += OnServiceStateChanged;

        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static void Detach()
    {
        if (_window is null)
            return;

        if (_service is not null)
            _service.StateChanged -= OnServiceStateChanged;

        _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _window.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        _window = null;
        _service = null;
        _settings = null;
        _activeTile = null;
        _escapeSpokenFor = null;
        _machine = null;
        IsRebinding = false;

        // The parsed gesture is static and would otherwise outlive the window it was parsed for. It is
        // keyed on the settings string by reference, so a later Attach re-parses anyway — this is about
        // not leaving state behind that nothing owns.
        _parsedFrom = null;
        _parsed = null;
    }

    private static void OnServiceStateChanged()
    {
        if (_service is { State: DictationState.Idle } && _machine is { IsRecording: true })
            _machine.Reset();
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_service is null || _machine is null || _settings is null)
                return;

            if (e.Key == Key.Escape
                && EscapeCancels(_service.State, _escapeSpokenFor?.Invoke() == true))
            {
                _service.Cancel();
                _machine.Reset();
                e.Handled = true;
                return;
            }

            if (!TryGetGesture(out var gesture) || !gesture.MatchesPress(e.Key, e.KeyModifiers))
                return;

            if (!MayActOnPress(_machine.IsRecording, IsHotkeyLive, CanRecord))
                return;

            _machine.KeyDown();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Dictation shortcut failed: {0}", ex);
        }
    }

    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_machine is null)
                return;
            if (!TryGetGesture(out var gesture) || !gesture.MatchesRelease(e.Key))
                return;

            // Forwarded in both modes, and that is the fix for a real bug: the machine tracks whether
            // the key is physically down so it can tell auto-repeat from a second press, and in toggle
            // mode it never saw a release, so a held shortcut stopped its own recording at the first
            // repeat.
            var wasRecording = _machine.IsRecording;
            _machine.KeyUp();

            if (ReleaseIsOurs(wasRecording, _settings?.Settings.Speech.Mode ?? DictationMode.PushToTalk,
                    isMainKey: e.Key == gesture.Key))
                e.Handled = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Dictation shortcut release failed: {0}", ex);
        }
    }

    private static string? _parsedFrom;
    private static HotkeyGesture? _parsed;

    /// <summary>
    /// Whether a press of the matched gesture may be acted on — the whole gating rule, as one function
    /// of things a test can hand it.
    /// </summary>
    /// <remarks>
    /// <para>The rest of this class is a static wired to a window, which is why the rule lives here
    /// instead of inside the handler: everything interesting about it is decided before any of that
    /// matters. Both halves have already been got wrong once — a recording nobody could stop, and a key
    /// swallowed on a machine with no model — and neither showed up anywhere but in use.</para>
    /// <para><paramref name="recording"/> wins outright: a recording this shortcut started must be
    /// stoppable by the same key that began it, whatever has changed since. In toggle mode the user can
    /// switch dictation off, disable the shortcut, or focus the rebinding box while the microphone is
    /// live, and every one of those used to leave a recording running to the five-minute cap with the
    /// tile's own microphone button hidden by the setting that caused it.</para>
    /// <para>Both gates are <b>functions</b>, not values, and the order is load-bearing:
    /// <paramref name="canRecord"/> stats the model file, and this runs for every keystroke the window
    /// sees.</para>
    /// </remarks>
    internal static bool MayActOnPress(bool recording, Func<bool> hotkeyLive, Func<bool> canRecord) =>
        recording || (hotkeyLive() && canRecord());

    /// <summary>
    /// Whether Escape belongs to dictation — which decides both that the recording is thrown away and
    /// that the key does not reach whatever is behind this handler.
    /// </summary>
    /// <remarks>
    /// <para>Only while <b>recording</b>. During transcription there is nothing on screen to abandon,
    /// the user has moved on, and Escape is a key whatever is in front of them wants — losing it in vim
    /// costs more than a transcript they can delete.</para>
    /// <para>And not when something else on screen has a stronger claim
    /// (<paramref name="spokenFor"/>). The settings dialog is an overlay in this window rather than a
    /// window of its own, so this handler tunnels past it: it cancelled the recording, marked the key
    /// handled, and left the dialog open with no way to close it. Dictating into a settings box is a
    /// feature, so the two are on screen together by design — and of the two meanings Escape has there,
    /// only closing the dialog has no other way of being said. The recording is still ended by the
    /// shortcut that started it.</para>
    /// </remarks>
    internal static bool EscapeCancels(DictationState state, bool spokenFor) =>
        state == DictationState.Recording && !spokenFor;

    /// <summary>
    /// Whether a release of the shortcut may be swallowed rather than passed on.
    /// </summary>
    /// <remarks>
    /// <para>Only a release that was <em>acted</em> on, which means push-to-talk and a recording that was
    /// running. Marking them handled in toggle mode quietly ate every space and alt release on its way
    /// to the terminal for as long as the recording lasted — and the child sees key-up events too,
    /// whenever it has asked for win32 input.</para>
    /// <para>And only the release of the <b>main key</b>. Letting go of Alt is what ends Alt+Space if the
    /// thumb comes off first, so the gesture has to hear about it — but the modifier's key-<em>down</em>
    /// was never swallowed, because a bare Alt does not match the gesture. Swallowing the matching key-up
    /// therefore left the child with an Alt it had seen pressed and would never see released: every menu
    /// mnemonic and every Alt-chord in the terminal fired for the rest of the session, after an ordinary
    /// dictation. The machine is still told; it is the terminal that must not be lied to.</para>
    /// </remarks>
    internal static bool ReleaseIsOurs(bool wasRecording, DictationMode mode, bool isMainKey) =>
        wasRecording && isMainKey && mode == DictationMode.PushToTalk;

    /// <summary>
    /// Whether the shortcut may take a keystroke that is not ending a recording of its own.
    /// </summary>
    /// <remarks>
    /// Separate from parsing the gesture, and that separation is the point: matching the key and being
    /// allowed to act on it are different questions, and a live recording answers the second one by
    /// itself.
    /// </remarks>
    private static bool IsHotkeyLive()
    {
        var speech = _settings?.Settings.Speech;
        // No separate on/off switch: an empty shortcut is what "off" means, and TryGetGesture already
        // fails to parse one. This asks only about the feature as a whole and about the rebinding box.
        if (speech is null || !speech.Enabled)
            return false;

        // The flag says what the user is doing; the focus check says it is still true. A dialog dismissed
        // in some way that never raises LostFocus would otherwise leave the shortcut switched off with
        // nothing on screen to explain why.
        return !(IsRebinding && _window?.FocusManager?.GetFocusedElement() is TextBox);
    }

    /// <summary>
    /// The configured gesture, parsed once per setting rather than once per keystroke.
    /// </summary>
    /// <remarks>
    /// This runs for **every key the window sees**, so it is on the path of ordinary typing in a
    /// terminal. Splitting a string to rebuild the same gesture on each one is pure waste.
    /// </remarks>
    private static bool TryGetGesture(out HotkeyGesture gesture)
    {
        gesture = default;

        var speech = _settings?.Settings.Speech;
        if (speech is null)
            return false;

        if (!ReferenceEquals(_parsedFrom, speech.Hotkey))
        {
            _parsed = HotkeyGesture.TryParse(speech.Hotkey, out var fresh) ? fresh : null;
            _parsedFrom = speech.Hotkey;
        }

        if (_parsed is null)
            return false;

        gesture = _parsed.Value;
        return true;
    }

    /// <summary>
    /// Whether dictation could actually run — asked <em>after</em> the key has already matched.
    /// </summary>
    /// <remarks>
    /// <para>A shortcut that cannot record must not take the key. Dictation is on by default and no model
    /// ships with the application, so on a fresh installation the gesture matched, swallowed Alt+Space
    /// before the shell ever saw it, and answered with a dialog — once per auto-repeat while the key was
    /// held. Until there is a model, the key belongs to the terminal.</para>
    /// <para>Asked last because it touches the disk: <c>IsReady</c> stats the model file, and on this
    /// path that would be two or three filesystem calls for every character typed into a terminal.</para>
    /// </remarks>
    private static bool CanRecord() => _service?.IsReady == true;

    private static void StartRecording()
    {
        if (_service is null || _settings is null)
            return;

        var tile = _activeTile?.Invoke();
        var focused = _window?.FocusManager?.GetFocusedElement();
        var speech = _settings.Settings.Speech;

        var started = _service.Start(tile ?? (object)"hotkey",
            text => DictationTextSink.Insert(tile, text, speech, focused));

        if (!started)
            _machine?.Reset();
    }

    private static void StopRecording() => _service?.Stop();
}
