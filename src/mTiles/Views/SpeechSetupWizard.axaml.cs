using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using mTiles.Services;
using mTiles.Services.Speech;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The dictation setup, walked through: a model, a microphone, and the shortcut used to prove them.
/// </summary>
/// <remarks>
/// One window for both occasions — the first run, where none of it is set up, and Settings → Speech,
/// where it is how somebody starts over. It replaced a single-screen prompt that asked which model to
/// download and nothing else, which could not answer the only question that matters: does this work.
/// </remarks>
public partial class SpeechSetupWizard : Window
{
    private SpeechSetupViewModel? _model;
    private SettingsService? _settings;
    private DictationService? _dictation;

    /// <summary>
    /// This window's own push-to-talk machine, so the last step can be done with the shortcut.
    /// </summary>
    /// <remarks>
    /// <para>Its own, and emphatically not <see cref="DictationHotkeys"/>: that is a static bound to one
    /// window, so attaching it here would tear the main window's shortcut down, and detaching on close
    /// would leave the application with no shortcut at all until it was restarted. A window of its own
    /// also means the main window's handler never sees these keys, so there is nothing to stand down.</para>
    /// <para>What is reused is the part that carries the behaviour —
    /// <see cref="DictationHotkeyMachine"/>, with its auto-repeat and release-grace rules already paid
    /// for. Only the wiring is here.</para>
    /// </remarks>
    private DictationHotkeyMachine? _machine;

    private Action? _onDictationStateChanged;

    public SpeechSetupWizard()
    {
        InitializeComponent();

        // Bubbling, as before: this is the plain "Escape closes the window", and it must stay behind
        // anything on the page that wants the key first — an open combo box, for one.
        KeyDown += OnKeyDown;

        // Tunnelling, because the shortcut has to be seen before a focused button treats Space as a
        // click, and because a push-to-talk needs the release as well as the press.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    /// <summary>
    /// The shortcut, and choosing a new one — the two things the same keys can mean on the last step.
    /// </summary>
    /// <remarks>
    /// <para>Capture wins outright while it is on, and it swallows the keystroke whatever it was: the
    /// user has said the next thing they press is the shortcut, and acting on it as well would start a
    /// recording with the key they were trying to bind. It lasts exactly one keystroke.</para>
    /// <para><b>Escape</b> is layered rather than overloaded. Choosing a shortcut, it abandons that;
    /// recording, it throws the recording away — which is what Escape means during dictation everywhere
    /// else in the application; otherwise it falls through to the bubbling handler above and closes the
    /// window, as it always did. Each meaning is the one thing the user could want at that moment, and
    /// pressing it twice still gets them out.</para>
    /// </remarks>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            HandleTunnelKeyDown(e);
        }
        catch (Exception ex)
        {
            // As DictationHotkeys does on the identical path, and for a reason that only applies here:
            // taking a press writes the new shortcut to settings, and that raises SettingsChanged to
            // whoever is listening — the theme bridge, the database tile, the main view model. A fault in
            // any one of them would surface as a failure of the keystroke. Caught so it is reported as
            // what it is rather than travelling up out of a key handler; the application survives it
            // either way, because CrashHandler marks dispatcher exceptions handled.
            Trace.TraceWarning("The setup wizard's shortcut failed: {0}", ex);
        }
    }

    private void HandleTunnelKeyDown(KeyEventArgs e)
    {
        if (TakesPress(e))
        {
            _claimedPresses.Add(e.Key);
            e.Handled = true;
            return;
        }

        // A press of a key a claim is outstanding on settles that claim afresh. Without this a release we
        // never saw — the window losing the keyboard mid-hold — would leave this handler swallowing that
        // key's release for the rest of the window's life, so Space would stop working on buttons with
        // nothing to explain it. Only that key: dropping every claim on any press would lose the one on
        // the shortcut the moment somebody touched another key while holding it, and their release would
        // then reach the focused button, which is the bug this whole mechanism exists to stop.
        _claimedPresses.Remove(e.Key);
    }

    /// <summary>Whether this handler takes the press for itself.</summary>
    private bool TakesPress(KeyEventArgs e)
    {
        if (_model is not { } model)
            return false;

        if (model.IsCapturingHotkey)
        {
            if (e.Key == Key.Escape)
            {
                model.CancelCaptureHotkeyCommand.Execute(null);
                return true;
            }

            // A modifier on its own, or Tab: not an answer, and not ours to swallow.
            return model.CaptureHotkey(e.Key, e.KeyModifiers);
        }

        if (e.Key == Key.Escape)
        {
            if (!model.IsRecordingHere)
                return false;

            _dictation?.Cancel();
            _machine?.Reset();
            return true;
        }

        if (_machine is null || !model.IsTestStep || !TryGetGesture(out var gesture)
            || !gesture.MatchesPress(e.Key, e.KeyModifiers))
            return false;

        // Told before the recording is attempted, and whether or not it succeeds: the hint this answers
        // is only ever about the keys reaching us. A press refused for want of a model has answered it.
        model.NoteHotkeyPressed();
        _machine.KeyDown();
        return true;
    }

    /// <summary>
    /// The keys whose presses this handler took, and whose releases it therefore owes nobody.
    /// </summary>
    /// <remarks>
    /// <para>Swallowing every release of the gesture's main key was too broad, and broke ordinary use:
    /// with <c>Alt+Space</c> bound, a bare Space — no Alt — is not the shortcut, so its press goes through
    /// to the focused button as it should, and swallowing its release meant the button never fired.
    /// Somebody with a Space shortcut could not press <b>Done</b>, or anything else, from the keyboard.
    /// What is owed is symmetry with the press, not with the key.</para>
    /// <para><b>A set, not one slot.</b> Two claimed presses really do overlap, and a single field let the
    /// second overwrite the first: hold <c>Alt+Space</c> — Space claimed, recording — then press
    /// <b>Escape</b> to abandon it, which is also a press this handler takes. The claim became Escape, so
    /// letting go of Space was no longer ours, reached the focused <b>Done</b> button and shut the whole
    /// wizard. That is the original reported bug, arrived at down a different path. Binding a new shortcut
    /// while the old one is held does the same thing.</para>
    /// </remarks>
    private readonly HashSet<Key> _claimedPresses = [];

    /// <summary>
    /// The release, which is what ends a push-to-talk.
    /// </summary>
    /// <remarks>
    /// <para><b>The release of the main key is swallowed, and it has to be.</b> A focused
    /// <see cref="Button"/> raises its Click from the key-<em>up</em> of Space and does not care that the
    /// key-down was marked handled — measured, not assumed. So with the default <c>Alt+Space</c> and the
    /// focus still on the footer button where the user left it by clicking Next, letting go of the
    /// shortcut pressed <b>Done</b>: the whole wizard shut on the first attempt to use dictation, at the
    /// exact moment the transcript was about to arrive. An earlier version of this comment said buttons
    /// do not care about a key-up. They do.</para>
    /// <para><b>Only the release whose press was taken</b>, which is not the same as every release of the
    /// gesture's key. With <c>Alt+Space</c> bound, a bare Space is not the shortcut: its press goes
    /// through to the focused button, and swallowing its release left that button never firing — somebody
    /// with a Space shortcut could not press <b>Done</b>, or anything else, from the keyboard. The first
    /// version of this fix had exactly that regression, and the test beside it was too weak to see it
    /// because it cleared the shortcut first. What is owed is symmetry with the press, not with the key.</para>
    /// <para><b>Deliberately not gated on <c>IsCapturingHotkey</c></b>, unlike the press above. The
    /// asymmetry has a direction: a capture claims a key<em>stroke</em>, because that is an instruction
    /// it is waiting for, but a release is a fact about the keyboard and belongs to whoever was told the
    /// key went down. Gating it breaks a reachable case — holding the shortcut in push-to-talk while
    /// speaking, clicking "use different keys" with the other hand, then letting go: the machine would
    /// never hear the release, would go on believing the key was held, and would record to its
    /// five-minute cap. Every case the gate would supposedly cover ends at <c>!IsRecording</c> inside the
    /// machine and does nothing.</para>
    /// </remarks>
    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        try
        {
            HandleTunnelKeyUp(e);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The setup wizard's shortcut release failed: {0}", ex);
        }
    }

    private void HandleTunnelKeyUp(KeyEventArgs e)
    {
        // Remove answers both questions at once: whether this release was ours, and clearing the claim.
        var claimed = _claimedPresses.Remove(e.Key);

        // The machine hears the release either way, and that is not the same question. Letting go of Alt
        // ends Alt+Space just as surely as letting go of Space, and the press of a bare modifier was
        // never claimed — so the gesture has to be told about a release this handler does not own.
        //
        // And not gated on the step, for the same reason it is not gated on the capture mode: a release
        // is a fact about the keyboard, and the machine is the only thing that knows a key was ever down.
        // Clicking Back while holding the shortcut moves the step *between* the press and the release, so
        // gating here meant the release was never delivered — the machine went on believing the key was
        // held, and the recording ran to its five-minute cap. The step change now abandons the recording
        // as well; this is the half that keeps the machine's own bookkeeping honest.
        if (_machine is not null && TryGetGesture(out var gesture) && gesture.MatchesRelease(e.Key))
            _machine.KeyUp();

        if (claimed)
            e.Handled = true;
    }

    /// <summary>
    /// The configured shortcut. Parsed on each keystroke, deliberately.
    /// </summary>
    /// <remarks>
    /// The main window's handler caches this because it runs for every character typed into a terminal.
    /// Here it runs for the handful of keys pressed in a modal setup window, and the shortcut changes
    /// under it whenever the user picks a new one — so a cache would be a stale-invalidation problem
    /// bought with nothing.
    /// </remarks>
    private bool TryGetGesture(out HotkeyGesture gesture)
    {
        gesture = default;
        return _settings is not null && HotkeyGesture.TryParse(_settings.Settings.Speech.Hotkey, out gesture);
    }

    /// <summary>
    /// Runs the wizard over <paramref name="owner"/> and returns when it is closed.
    /// </summary>
    /// <remarks>
    /// The view model is disposed here rather than by whoever opened it: it holds a subscription to the
    /// dictation service, which outlives this window, and it may be holding the microphone — closing the
    /// window mid-sentence has to give both back.
    /// </remarks>
    public static async Task ShowAsync(Window owner, DictationService dictation, SettingsService settings)
    {
        var model = new SpeechSetupViewModel(dictation, settings);
        var window = new SpeechSetupWizard { DataContext = model };
        window.Bind(model, dictation, settings);

        model.CloseRequested += window.Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            model.CloseRequested -= window.Close;
            model.Dispose();
        }
    }

    /// <summary>Wires this window to the services it drives. Internal so a test can build the window
    /// without a modal owner — the shortcut's routing is the part that broke in use.</summary>
    internal void Bind(SpeechSetupViewModel model, DictationService dictation, SettingsService settings)
    {
        // Whatever a previous bind left attached. Nothing calls this twice today — it is one line from
        // ShowAsync and one from a test — but the subscription is to a service that outlives this window
        // by the life of the application, and "attach without detaching" is the shape that turns a second
        // caller into a window that never dies. Paired with the detach in OnClosed rather than relying on
        // it, because only one of the two is guaranteed to run.
        Unlisten();

        _model = model;
        _dictation = dictation;
        _settings = settings;

        _machine = new DictationHotkeyMachine(
            () => settings.Settings.Speech.Mode,
            () =>
            {
                // A refusal — no model, a busy microphone — leaves the machine believing it is recording,
                // and the next press would then be read as the one that stops it. The message the service
                // put on screen would be followed by a shortcut that appears to do nothing.
                if (!model.StartTest())
                    _machine?.Reset();
            },
            model.StopTest);

        // Whatever else ends the recording — the button, an error, the transcript arriving — the machine
        // has to hear about it, or in toggle mode the next press spends itself switching off something
        // that has already stopped.
        _onDictationStateChanged = () =>
        {
            if (dictation.State == DictationState.Idle && _machine is { IsRecording: true })
                _machine.Reset();
        };
        dictation.StateChanged += _onDictationStateChanged;
    }

    /// <summary>Lets go of the dictation service, which outlives this window by the life of the
    /// application. Safe to call when nothing is attached.</summary>
    private void Unlisten()
    {
        if (_dictation is not null && _onDictationStateChanged is not null)
            _dictation.StateChanged -= _onDictationStateChanged;

        _onDictationStateChanged = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        Unlisten();
        _machine = null;

        // Also here, because a window can be closed by the title bar without going through ShowAsync's
        // finally in any way it could rely on. Disposing twice is harmless; leaving a recording running
        // is not.
        _model?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
