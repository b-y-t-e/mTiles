using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

/// <summary>
/// Setting dictation up, once, in the order the pieces depend on each other: a model, a microphone, and
/// a sentence to prove the two.
/// </summary>
/// <remarks>
/// <para>Shown on a first run, where nothing is set up, and from Settings → Speech, where it is how you
/// start over. The same object both times: two windows saying the same thing is two windows to keep in
/// step, and the first-run one used to be a single screen that asked about the model and nothing else —
/// it could not tell anybody whether dictation actually worked.</para>
/// <para>It owns no state of its own beyond the step it is on. The model, the device and the transcript
/// all live where they already lived: the catalogue, the settings file, the dictation service.</para>
/// </remarks>
public sealed partial class SpeechSetupViewModel : ObservableObject, IDisposable
{
    private readonly DictationService _dictation;
    private readonly SettingsService _settings;

    /// <summary>Runs an action after a delay — a dispatcher timer, in the application.</summary>
    /// <remarks>Injected for the same reason <c>DictationHotkeyMachine</c> takes one: the last step's
    /// hint is a rule about time, and a test of it should not have to wait twelve seconds or start a
    /// dispatcher.</remarks>
    private readonly Action<TimeSpan, Action> _schedule;

    internal SpeechSetupViewModel(DictationService dictation, SettingsService settings,
        Action<TimeSpan, Action>? schedule = null)
    {
        _dictation = dictation;
        _settings = settings;
        _schedule = schedule ?? ((delay, action) => Avalonia.Threading.DispatcherTimer.RunOnce(action, delay));

        var chosen = settings.Settings.Speech.ModelId;

        // The recommended few, plus whatever the user is actually using if it is not among them. The
        // list is editorial — three of six — so somebody who chose Whisper Large in Settings and then
        // opens this window would otherwise be shown three models, none of them theirs.
        var offered = SpeechModelCatalog.Recommended
            .Concat(SpeechModelCatalog.All.Where(m =>
                m.Id == chosen && !SpeechModelCatalog.Recommended.Contains(m)));

        foreach (var model in offered)
        {
            var row = new SpeechModelViewModel(model, dictation.Store, model.Id == chosen)
            {
                SelectRequested = Choose,
                AvailabilityChanged = _ => RefreshModelReady(),
                // Wired here as well as in the Speech tab: a row that cannot unload its own model cannot
                // replace its files either, and which of the two lists the click came from is not
                // something the download should depend on.
                ReleaseModel = () => dictation.UnloadModel(dictation.Store.GetPath(model)),
            };
            Models.Add(row);
        }

        // Whatever is already usable is what the wizard opens on, so somebody re-running it does not
        // have to choose again to get past the first step.
        //
        // The *field*, not the property: setting the property runs Choose, which writes the model to
        // settings. Opening this window is not choosing anything, and on a configuration the list cannot
        // represent — a model that is downloaded but not offered here — the fallback to "the first row"
        // would silently replace a working setting with one whose file is not even on the machine. A
        // window that breaks dictation by being opened is the opposite of what it is for.
        _selected = Models.FirstOrDefault(m => m.IsInUse)
            ?? Models.FirstOrDefault(m => m.IsDownloaded)
            ?? Models.FirstOrDefault();

        RefreshModelReady();

        // What was working when this opened, so that closing half-way through cannot take it away.
        _workingAtOpen = IsModelReady ? settings.Settings.Speech.ModelId : null;

        // Adopting the preselection is right only when there is nothing to lose: no model configured, or
        // one configured whose file is not on the machine. Then a row is highlighted and the setting
        // agrees with it, which is what the step is for. When the configuration already works, the
        // highlight is a highlight and nothing is written.
        if (!IsModelReady && _selected is { } preselected)
            Choose(preselected);

        _device = string.IsNullOrEmpty(settings.Settings.Speech.InputDeviceName)
            ? SettingsViewModel.DefaultDeviceOption
            : settings.Settings.Speech.InputDeviceName;

        RefreshHotkey();
        RefreshAvailability();

        _dictation.StateChanged += OnDictationChanged;

        // While this window is up it is the one that has to say what went wrong. The application's own
        // handler puts these in a message box owned by the main window — behind a modal wizard, where
        // nobody can see or dismiss it — so a failed test step would look like nothing happening.
        _dictation.Error += OnDictationError;
    }

    private void OnDictationError(string message) => Message = message;

    /// <summary>
    /// The model that was configured and present when this window opened, or null if none was.
    /// </summary>
    /// <remarks>
    /// Picking a model in the list writes it to settings immediately — which is what makes the list a
    /// choice rather than a form to submit — and the first step then holds the user there until it is
    /// downloaded. Closing the window at that point used to leave dictation configured for a model that
    /// is not on the machine: a working setup broken by opening a window and thinking better of it. The
    /// choice is kept when there was nothing working to go back to, because then it is the only answer
    /// there is; and the download itself is kept either way, in its <c>.partial</c> file.
    /// </remarks>
    private readonly string? _workingAtOpen;

    private bool _disposed;

    public ObservableCollection<SpeechModelViewModel> Models { get; } = [];

    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty]
    private SpeechSetupStep _step = SpeechSetupStep.Model;

    [ObservableProperty]
    private SpeechModelViewModel? _selected;

    [ObservableProperty]
    private string _device;

    /// <summary>What came back from the microphone in the last step. Empty until something does.</summary>
    [ObservableProperty]
    private string _transcript = "";

    /// <summary>Anything the user needs told: no model, a busy microphone, a failed transcription.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTranscribing;

    /// <summary>True when the chosen model is on this machine — the one condition that blocks the first
    /// step, and the reason the other two exist.</summary>
    [ObservableProperty]
    private bool _isModelReady;

    // ─── the shortcut, taught on the last step by being used ───

    /// <summary>The configured shortcut, one chip per key: <c>Alt</c> <c>Space</c>. Empty when there is
    /// none.</summary>
    /// <remarks>Keys rather than the string <c>Alt+Space</c>, because the step is an instruction to press
    /// something, and a text box saying <c>Alt+Space</c> reads as a value to edit.</remarks>
    public ObservableCollection<string> HotkeyKeys { get; } = [];

    /// <summary>True when there is a shortcut that can actually be listened for, so there are keys to
    /// show and an instruction to give.</summary>
    [ObservableProperty]
    private bool _hasHotkey;

    /// <summary>
    /// True when the setting names nothing at all — which is a real answer, not a missing one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="HasHotkey"/> because they differ in exactly the case that used to lie: a
    /// setting naming something unusable has no keys to show <em>and</em> is not the user saying "none".
    /// It is what makes "No shortcut — the microphone button still works" a statement about their choice
    /// rather than about our failure to parse it.
    /// </remarks>
    [ObservableProperty]
    private bool _isShortcutBlank;

    /// <summary>The same sentence the Speech tab shows about a shortcut with no modifier — or about one
    /// this application cannot listen for.</summary>
    [ObservableProperty]
    private string? _hotkeyWarning;

    /// <summary>
    /// Whether the shortcut is held or pressed — which the step has to say correctly, not assume.
    /// </summary>
    /// <remarks>
    /// <para>The mode is chosen in Settings → Speech and this window does not offer it, which is exactly
    /// how the instruction came to be wrong: it said "Hold" to everybody. In toggle mode
    /// <c>DictationHotkeyMachine</c> ignores the release, so somebody following that instruction held the
    /// keys, spoke, let go — and the recording carried on to its five-minute cap with "Listening…" on
    /// screen and no transcript ever arriving. The step that exists to prove dictation works was proving
    /// the opposite, and doing it with its own words.</para>
    /// <para>Read from settings rather than offered here. Adding the switch to this page would double the
    /// explanation for a preference almost nobody has on a first run; telling the truth about it costs a
    /// verb.</para>
    /// </remarks>
    [ObservableProperty]
    private bool _isPushToTalk = true;

    /// <summary>"Hold" or "Press", in front of the keys.</summary>
    public string HotkeyVerb => IsPushToTalk ? "Hold" : "Press";

    /// <summary>The second half of a toggle-mode instruction, which push-to-talk does not need.</summary>
    public string? HotkeyFollowUp =>
        IsPushToTalk ? null : "Press it again when you have finished speaking.";

    /// <summary>
    /// Whether to tell the user to press the keys at all.
    /// </summary>
    /// <remarks>
    /// There has to be a shortcut, and it has to be able to do something. With dictation switched off the
    /// page showed "Hold Alt Space and say something" directly beneath a notice saying nothing will
    /// record — an instruction and its own refutation, one line apart. Only the switch applies then.
    /// <para>Computed here rather than as two bindings in the view, because a binding cannot say "and".
    /// </para>
    /// </remarks>
    public bool ShowsShortcutInstruction => HasHotkey && !IsDictationOff;

    /// <summary>The toggle-mode second line, which is part of the same instruction.</summary>
    public bool ShowsToggleFollowUp => ShowsShortcutInstruction && !string.IsNullOrEmpty(HotkeyFollowUp);

    /// <summary>"No shortcut — the microphone button still works", which is only true while the feature
    /// is on.</summary>
    public bool ShowsNoShortcutNote => IsShortcutBlank && !IsDictationOff;

    partial void OnIsPushToTalkChanged(bool value)
    {
        OnPropertyChanged(nameof(HotkeyVerb));
        OnPropertyChanged(nameof(HotkeyFollowUp));
        OnPropertyChanged(nameof(ShowsToggleFollowUp));
    }

    partial void OnHasHotkeyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsShortcutInstruction));
        OnPropertyChanged(nameof(ShowsToggleFollowUp));
    }

    partial void OnIsShortcutBlankChanged(bool value) => OnPropertyChanged(nameof(ShowsNoShortcutNote));

    partial void OnIsDictationOffChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsShortcutInstruction));
        OnPropertyChanged(nameof(ShowsToggleFollowUp));
        OnPropertyChanged(nameof(ShowsNoShortcutNote));
    }

    /// <summary>
    /// True while the next keystroke will be bound rather than acted on.
    /// </summary>
    /// <remarks>
    /// <para>An explicit mode that lasts exactly one keystroke, rather than the Speech tab's "for as long
    /// as this box has focus". On this step the same keys mean two different things — start recording, or
    /// become the shortcut — so the mode has to be visible and it has to end by itself. Focus is neither:
    /// it is invisible, and it ends whenever the user happens to click something.</para>
    /// <para>It is also why the tab needs <c>DictationHotkeys.BeginRebinding</c> and this does not. That
    /// scope exists because the settings dialog is an overlay inside the main window, which the global
    /// shortcut handler tunnels through. The wizard is a window of its own, so that handler never sees
    /// these keys at all — the only listener to stand down for is this one, and this flag is it.</para>
    /// </remarks>
    [ObservableProperty]
    private bool _isCapturingHotkey;

    /// <summary>
    /// Shown when the user has been on the last step for a while without the shortcut ever arriving.
    /// </summary>
    /// <remarks>
    /// The only failure on this step that produces nothing at all to look at. See
    /// <see cref="SpeechSetupFlow.ShortcutHintDelay"/> for why it waits rather than warning up front.
    /// </remarks>
    [ObservableProperty]
    private bool _showHotkeyHint;

    /// <summary>Which visit to the last step a pending hint belongs to, so one scheduled for a visit the
    /// user has left cannot fire over a later one.</summary>
    private int _hintGeneration;

    /// <summary>
    /// True when the feature itself is switched off, so nothing on the last step can work.
    /// </summary>
    /// <remarks>
    /// <para>Reachable, and the wizard used to walk straight into it: <b>Set up dictation…</b> is not
    /// gated on the switch, so anybody who turns dictation off and then opens the wizard reaches a page
    /// saying "Hold Alt+Space and say something" — an instruction that cannot succeed. What came back was
    /// the service's own refusal, <i>"Dictation is switched off. Turn it on in Settings → Speech"</i>,
    /// pointing at the window this modal is covering. A dead end made of two correct components.</para>
    /// <para>Not fixed by hiding the button that opens the wizard: configuring dictation before switching
    /// it on is a perfectly reasonable order to do things in. Fixed by saying so on the page and offering
    /// the switch there, which is what the rest of this window does with every other question.</para>
    /// </remarks>
    [ObservableProperty]
    private bool _isDictationOff;

    /// <summary>Switches the feature on, from the page that needs it.</summary>
    /// <remarks>
    /// A button rather than something this window does on its own. Somebody turned it off, and running a
    /// setup wizard is not clear enough evidence that they changed their mind — but pressing "Turn
    /// dictation on" is.
    /// </remarks>
    [RelayCommand]
    private void TurnDictationOn()
    {
        _settings.Settings.Speech.Enabled = true;
        _settings.NotifyChanged();
        RefreshAvailability();
        Message = null;
        ArmHotkeyHint(Step == SpeechSetupStep.Test);
    }

    private void RefreshAvailability() => IsDictationOff = !_settings.Settings.Speech.Enabled;

    public bool IsModelStep => Step == SpeechSetupStep.Model;
    public bool IsMicrophoneStep => Step == SpeechSetupStep.Microphone;
    public bool IsTestStep => Step == SpeechSetupStep.Test;

    public string StepCaption => $"Step {SpeechSetupFlow.NumberOf(Step)} of {SpeechSetupFlow.Steps.Count}";
    public bool CanGoBack => SpeechSetupFlow.Previous(Step) is not null;
    public bool CanGoNext => SpeechSetupFlow.CanLeave(Step, IsModelReady);
    public string NextCaption => SpeechSetupFlow.IsLast(Step) ? "Done" : "Next";

    /// <summary>Raised when the wizard is finished with — the view closes the window.</summary>
    public event Action? CloseRequested;

    partial void OnStepChanged(SpeechSetupStep value)
    {
        OnPropertyChanged(nameof(IsModelStep));
        OnPropertyChanged(nameof(IsMicrophoneStep));
        OnPropertyChanged(nameof(IsTestStep));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextCaption));
        Message = null;

        if (value == SpeechSetupStep.Microphone)
            _ = SafeLoadDevicesAsync();

        // Leaving the step takes the capture mode with it. Otherwise somebody who clicks "use a
        // different shortcut" and then Back arrives on an earlier page where the next key they press is
        // silently swallowed into a shortcut they were not choosing any more.
        IsCapturingHotkey = false;

        // And it takes the recording. Holding the shortcut and clicking Back with the other hand left
        // the microphone open with nothing on screen to say so — "Listening…" belongs to the step that
        // has just gone — until the service's own five-minute cap closed it. Abandoned rather than
        // stopped: the transcript would arrive in a box the user has navigated away from, so there is
        // nothing to deliver and the only thing worth doing is giving the device back.
        AbandonTest();

        RefreshAvailability();
        ArmHotkeyHint(value == SpeechSetupStep.Test);
    }

    /// <summary>Starts — or abandons — the wait before the "nothing happened?" hint.</summary>
    private void ArmHotkeyHint(bool armed)
    {
        var generation = ++_hintGeneration;
        ShowHotkeyHint = false;

        // Not when the feature is switched off. The hint blames the desktop for taking the shortcut, and
        // saying that to somebody whose dictation is simply turned off sends them to fix the wrong thing
        // — the same reason a press that is refused still counts as the keys having arrived.
        if (!armed || !HasHotkey || IsDictationOff)
            return;

        _schedule(SpeechSetupFlow.ShortcutHintDelay, () =>
        {
            if (generation == _hintGeneration)
                ShowHotkeyHint = true;
        });
    }

    /// <summary>
    /// The shortcut reached us. Whatever happens next, the hint has been answered.
    /// </summary>
    /// <remarks>
    /// Called when the gesture <em>matches</em>, not when a recording starts: a press that is refused for
    /// want of a model has still told us the keys are getting through, which is the only thing the hint
    /// is about. Reporting the wrong problem is worse than reporting none.
    /// </remarks>
    internal void NoteHotkeyPressed()
    {
        _hintGeneration++;
        ShowHotkeyHint = false;
    }

    partial void OnIsModelReadyChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    partial void OnSelectedChanged(SpeechModelViewModel? value)
    {
        if (value is not null)
            Choose(value);
    }

    partial void OnDeviceChanged(string value)
    {
        // An empty selection is the combo box rebuilding its items, not a choice — the same trap the
        // settings tab has, and the same answer.
        if (string.IsNullOrEmpty(value))
            return;

        _settings.Settings.Speech.InputDeviceName =
            value == SettingsViewModel.DefaultDeviceOption ? "" : value;
        _settings.NotifyChanged();
    }

    /// <summary>Re-reads the shortcut from settings into what the step shows.</summary>
    /// <remarks>The mode comes along with it, because the instruction is one sentence about both and a
    /// half-refreshed sentence is how it came to be wrong in the first place.</remarks>
    private void RefreshHotkey()
    {
        IsPushToTalk = _settings.Settings.Speech.Mode == mTiles.Models.DictationMode.PushToTalk;

        var parsed = HotkeyGesture.TryParse(_settings.Settings.Speech.Hotkey, out var gesture)
            ? gesture
            : (HotkeyGesture?)null;

        HotkeyKeys.Clear();
        foreach (var part in parsed?.GetParts() ?? [])
            HotkeyKeys.Add(part);

        HasHotkey = parsed is not null;

        // Three states, not two. A setting naming something this application cannot listen for is not the
        // same as naming nothing: the step used to answer both with "No shortcut — use the microphone
        // button", so a hand-edited or future-version settings file produced a page that told the user
        // they had made a choice they had not made, and offered no sign that anything was wrong. The
        // Speech tab reports it; two windows disagreeing about one setting is the failure this whole
        // feature keeps guarding against.
        IsShortcutBlank = string.IsNullOrWhiteSpace(_settings.Settings.Speech.Hotkey);
        HotkeyWarning = HotkeyAdvice.ForSetting(_settings.Settings.Speech.Hotkey);
    }

    /// <summary>
    /// The next keystroke becomes the shortcut.
    /// </summary>
    /// <remarks>
    /// A recording of ours is ended first, because the two modes contradict each other on screen and in
    /// the keyboard. "Listening…" and "Press the keys you want" were shown together — reachable by
    /// starting a recording with the Record button, or in toggle mode with the shortcut, and then
    /// clicking through — and every key the user pressed to end the recording was bound as a shortcut
    /// instead, leaving it running to its five-minute cap. Ended rather than refused: the click says
    /// plainly enough what they want to do next, and stopping transcribes, so nothing they said is lost.
    /// </remarks>
    [RelayCommand]
    private void BeginCaptureHotkey()
    {
        StopTest();
        Message = null;
        IsCapturingHotkey = true;
    }

    [RelayCommand]
    private void CancelCaptureHotkey() => IsCapturingHotkey = false;

    /// <summary>
    /// Turns the shortcut off, which is the same thing as having no shortcut.
    /// </summary>
    /// <remarks>
    /// Offered quietly, and offered at all because without it this step reads as a demand. The
    /// microphone button in the tile's own header does not go away, so "no shortcut" costs the user
    /// nothing but the keys back — and somebody who feels cornered here clears it afterwards in Settings,
    /// or more often does not find where.
    /// </remarks>
    [RelayCommand]
    private void ClearHotkey()
    {
        IsCapturingHotkey = false;
        SetHotkey("");
    }

    /// <summary>
    /// Binds <paramref name="key"/> if the step is waiting for one.
    /// </summary>
    /// <returns>True when the keystroke was taken and must not be acted on further.</returns>
    /// <remarks>
    /// Which keystrokes are an answer is <see cref="HotkeyCapture"/>, shared with the Speech tab — down
    /// to Backspace meaning "none" and Escape being left alone, which is what lets the window keep using
    /// it to cancel.
    /// </remarks>
    internal bool CaptureHotkey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (!IsCapturingHotkey)
            return false;

        var capture = HotkeyCapture.Interpret(key, modifiers);
        if (!capture.Taken)
            return false;

        // Back to the instruction immediately, with the new keys on it. A capture mode that has to be
        // dismissed is one more thing to explain; one keystroke in, one keystroke out.
        IsCapturingHotkey = false;
        SetHotkey(capture.Action == HotkeyCaptureAction.Clear ? "" : capture.Gesture.ToString());
        return true;
    }

    /// <summary>
    /// Writes the shortcut straight to settings, like every other answer in this window.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> restored on close, unlike the model. That restore exists because choosing
    /// a model can be left half-done — picked but not downloaded — and closing on that leaves dictation
    /// pointing at a file that is not there. A captured gesture has no half-done state: it is complete
    /// and usable the moment it is pressed, so there is nothing to go back from.
    /// </remarks>
    private void SetHotkey(string value)
    {
        _settings.Settings.Speech.Hotkey = value;
        _settings.NotifyChanged();
        RefreshHotkey();

        // A shortcut that has just been cleared has nothing to wait for; one that has just been set is
        // the user telling us they are about to try it.
        ArmHotkeyHint(Step == SpeechSetupStep.Test);
    }

    private void Choose(SpeechModelViewModel row)
    {
        foreach (var model in Models)
            model.IsSelected = ReferenceEquals(model, row);

        _settings.Settings.Speech.ModelId = row.Id;
        _settings.NotifyChanged();
        RefreshModelReady();
    }

    /// <summary>
    /// Whether the model dictation would actually use is on this machine.
    /// </summary>
    /// <remarks>
    /// Asked of the service rather than of the rows: <c>SelectedModel</c> is the setting, or the
    /// catalogue's default when the setting names nothing usable, and that is what a recording will try
    /// to load. Counting rows instead made the wizard block on its first step for somebody whose chosen
    /// model is downloaded and working but not one of the three it offers.
    /// </remarks>
    private void RefreshModelReady()
    {
        foreach (var row in Models)
            row.RefreshDownloaded();

        IsModelReady = _dictation.SelectedModel is { } model && _dictation.Store.IsDownloaded(model);
    }

    /// <summary>Fetches the model the user picked. Progress and cancelling belong to the row itself.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selected is not { IsDownloading: false, IsDownloaded: false } row)
            return;

        Message = null;
        await row.DownloadCommand.ExecuteAsync(null);
        Message = row.Error;
        RefreshModelReady();
    }

    /// <summary>
    /// Nothing awaits this, so nothing would see it fail.
    /// </summary>
    /// <remarks>
    /// Enumerating devices swallows its own errors today, so this catches nothing yet — which is the
    /// point: an unhandled fault on a task nobody awaits reaches <c>CrashHandler</c> as an unobserved
    /// exception instead of the one place the user is looking. The settings tab starts the same call the
    /// same way and guards it; two spellings of one pattern is how the unguarded one survives.
    /// </remarks>
    private async Task SafeLoadDevicesAsync()
    {
        try
        {
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            Message = $"The microphones could not be listed: {ex.Message}";
        }
    }

    private async Task LoadDevicesAsync()
    {
        // Off the thread, like everywhere else this is asked: the first call is native portaudio
        // initialising, which is most of a second.
        // Rescanning, unlike the tab's own fill: this step is "choose your microphone", and plugging one
        // in is exactly what somebody does while they are on it. The backend only enumerates when it is
        // initialised, so without this a headset connected a minute ago is not on the list.
        var names = await Task.Run(() => _dictation.GetInputDevices(rescan: true)).ConfigureAwait(true);
        var configured = _settings.Settings.Speech.InputDeviceName;

        Devices.Clear();
        Devices.Add(SettingsViewModel.DefaultDeviceOption);
        foreach (var name in names)
            Devices.Add(name);

        // A device that is not plugged in right now stays in the list and stays chosen.
        if (!string.IsNullOrEmpty(configured) && !Devices.Contains(configured))
            Devices.Add(configured);

        Device = string.IsNullOrEmpty(configured) ? SettingsViewModel.DefaultDeviceOption : configured;

        if (names.Count == 0)
            Message = "No microphone was found. Dictation needs one; the rest of this is still worth setting up.";
    }

    /// <summary>Records, or ends the recording this wizard started.</summary>
    [RelayCommand]
    private void ToggleTest()
    {
        // Only a *recording* is ended by this button. The same shape with `!= Idle` — which is what this
        // was, copied from the tile before that one was fixed — sends a click during transcription into
        // Stop, which returns early when nothing is recording: the button answers with nothing at all.
        // Falling through to Start gets it refused with a reason instead.
        if (IsRecordingHere)
        {
            StopTest();
            return;
        }

        StartTest();
    }

    /// <summary>Whether the recording that is running is the one this window started.</summary>
    internal bool IsRecordingHere =>
        _dictation.State == DictationState.Recording && ReferenceEquals(_dictation.Owner, this);

    /// <summary>Starts the trial recording.</summary>
    /// <returns>False when the service refused, having said why.</returns>
    /// <remarks>
    /// Split out of the button so the shortcut can reach it too — the whole point of the step is that
    /// both routes are the same recording, and the button is the fallback for a shortcut the desktop has
    /// taken. The two must not drift into two slightly different trials.
    /// </remarks>
    internal bool StartTest()
    {
        Message = null;
        Transcript = "";

        // Straight into this window rather than through DictationTextSink: the point of the step is to
        // show the words, not to type them somewhere.
        return _dictation.Start(this, text =>
        {
            Transcript = text;
            return true;
        });
    }

    /// <summary>Ends a recording this window started, and only one it started.</summary>
    internal void StopTest()
    {
        if (IsRecordingHere)
            _dictation.Stop();
    }

    /// <summary>
    /// Throws away whatever this window has running, at whatever stage.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="StopTest"/>, and the difference is whether there is anywhere for the
    /// words to go. Stopping transcribes, which is right when the user is staying to read the result;
    /// this is for the cases where they have gone — a step change, the window closing — and a transcript
    /// would be delivered to a box nobody is looking at while the microphone stayed open in the meantime.
    /// Asked of the owner rather than of <see cref="IsRecordingHere"/> so that a transcription already
    /// under way is abandoned too.
    /// </remarks>
    private void AbandonTest()
    {
        if (ReferenceEquals(_dictation.Owner, this))
            _dictation.Cancel();
    }

    private void OnDictationChanged()
    {
        var mine = ReferenceEquals(_dictation.Owner, this);
        IsRecording = mine && _dictation.State == DictationState.Recording;
        IsTranscribing = mine && _dictation.State == DictationState.Transcribing;
    }

    [RelayCommand]
    private void Back()
    {
        if (SpeechSetupFlow.Previous(Step) is { } previous)
            Step = previous;
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
            return;

        if (SpeechSetupFlow.Next(Step) is { } next)
            Step = next;
        else
            CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>
    /// Takes the microphone back if the wizard is closed mid-sentence, and lets go of the service.
    /// </summary>
    /// <remarks>
    /// <para>The service outlives this window by a long way. A recording left running would have nowhere
    /// to deliver to and would hold the microphone until its five-minute cap; a handler left subscribed
    /// would keep the whole wizard alive behind it.</para>
    /// <para>A download in progress is stopped for the same reason and not out of thrift: the only
    /// reference to its cancellation is the row that just left the screen, so half a gigabyte would go on
    /// arriving with no progress shown and no way to stop it. Nothing is thrown away — the bytes stay in
    /// the <c>.partial</c> file and the next attempt resumes from them, which is what that file is
    /// for.</para>
    /// <para>Once, like every other <c>Dispose</c> here — and stated rather than relied upon. A second
    /// call happens to be harmless today: the unsubscribes are, and the restore below re-reads the
    /// setting it just wrote and finds nothing to do. That is an accident of the current steps, not a
    /// property of the method, and the steps are exactly the kind that grow. The window's own teardown
    /// and a caller tidying up can both reach this.</para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _dictation.StateChanged -= OnDictationChanged;
        _dictation.Error -= OnDictationError;

        // A hint scheduled twelve seconds ago outlives this window on a dispatcher timer. Firing it
        // changes nothing anyone can see, but a timer that reaches into a disposed view model is the
        // sort of thing that stops being harmless the moment somebody gives the hint something to do.
        _hintGeneration++;

        if (ReferenceEquals(_dictation.Owner, this))
            _dictation.Cancel();

        foreach (var model in Models)
            model.CancelDownloadCommand.Execute(null);

        // Half-finished is not a configuration. If what is chosen now is not on the machine and
        // something that was is, that is what dictation goes back to.
        RefreshModelReady();
        if (!IsModelReady && _workingAtOpen is { } working
            && !string.Equals(working, _settings.Settings.Speech.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Settings.Speech.ModelId = working;
            _settings.NotifyChanged();
        }
    }
}
