using Avalonia.Input;
using mTiles.Models;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The setup wizard: which step follows which, when the user may move on, and what closing it in the
/// middle has to give back.
/// </summary>
public class SpeechSetupTests : IDisposable
{
    private sealed class FakeCapture : IAudioCapture
    {
        public bool IsAvailable => true;
        public bool IsRecording { get; private set; }
        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["Yeti"];

        public void Start(string deviceName) => IsRecording = true;

        private sealed record Handle : IRecordingHandle;

        public IRecordingHandle? Detach()
        {
            if (!IsRecording)
                return null;

            IsRecording = false;
            return new Handle();
        }

        public float[] Finish(IRecordingHandle? detached) => new float[16_000];
        public void Dispose() { }
    }

    private sealed class SilentEngine : ISpeechToTextEngine
    {
        public bool IsLoaded => false;
        public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Unload() { }
        public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult("said something");
        public void Dispose() { }
    }

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public SpeechSetupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void PlaceOnDisk(string modelId)
    {
        var model = SpeechModelCatalog.Find(modelId)!;
        using var file = File.Create(Path.Combine(_directory, model.FileName));
        file.SetLength(model.DownloadBytes);
    }

    /// <summary>
    /// Stands in for the dispatcher timer behind the last step's hint.
    /// </summary>
    /// <remarks>
    /// The hint waits twelve seconds by design, and no test should. Collecting the callbacks instead of
    /// running them also makes the interesting question askable: not only "does it fire" but "was one
    /// scheduled at all", which is what tells a hint that never arms apart from one that arms and is
    /// then cancelled.
    /// </remarks>
    private sealed class ManualSchedule
    {
        private readonly List<Action> _pending = [];

        public void Schedule(TimeSpan _, Action action) => _pending.Add(action);

        public int Scheduled => _pending.Count;

        /// <summary>Runs everything scheduled so far, as the dispatcher would when the delays elapse.</summary>
        public void Elapse()
        {
            var due = _pending.ToList();
            _pending.Clear();
            foreach (var action in due)
                action();
        }
    }

    private (SpeechSetupViewModel Wizard, DictationService Dictation, TempSettings Settings) Build(
        SpeechModelStore? store = null, ManualSchedule? schedule = null)
    {
        var settings = new TempSettings();
        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            store ?? new SpeechModelStore(_directory), action => action());

        return (new SpeechSetupViewModel(dictation, settings.Service, schedule is null ? null : schedule.Schedule),
            dictation, settings);
    }

    /// <summary>A server that accepts the request and then says nothing until it is cancelled.</summary>
    private sealed class NeverAnsweringServer : HttpMessageHandler
    {
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    // ---- the flow itself, with nothing attached ----

    [Fact]
    public void The_steps_run_model_then_microphone_then_test()
    {
        Assert.Equal(
            [SpeechSetupStep.Model, SpeechSetupStep.Microphone, SpeechSetupStep.Test],
            SpeechSetupFlow.Steps);

        Assert.Equal(SpeechSetupStep.Microphone, SpeechSetupFlow.Next(SpeechSetupStep.Model));
        Assert.Equal(SpeechSetupStep.Test, SpeechSetupFlow.Next(SpeechSetupStep.Microphone));
        Assert.Null(SpeechSetupFlow.Next(SpeechSetupStep.Test));

        Assert.Null(SpeechSetupFlow.Previous(SpeechSetupStep.Model));
        Assert.Equal(SpeechSetupStep.Microphone, SpeechSetupFlow.Previous(SpeechSetupStep.Test));
        Assert.True(SpeechSetupFlow.IsLast(SpeechSetupStep.Test));
    }

    /// <summary>
    /// Only the model step blocks, and only until there is a model.
    /// </summary>
    /// <remarks>
    /// The microphone step must never block: the system default is a real answer, and a machine with no
    /// audio at all would otherwise trap somebody on a page they cannot satisfy.
    /// </remarks>
    [Theory]
    [InlineData(SpeechSetupStep.Model, false, false)]
    [InlineData(SpeechSetupStep.Model, true, true)]
    [InlineData(SpeechSetupStep.Microphone, false, true)]
    [InlineData(SpeechSetupStep.Test, false, true)]
    public void Only_the_model_step_can_hold_the_user_up(SpeechSetupStep step, bool modelReady, bool expected)
        => Assert.Equal(expected, SpeechSetupFlow.CanLeave(step, modelReady));

    // ---- the wizard over a real service ----

    [Fact]
    public void A_fresh_installation_starts_on_the_model_step_and_cannot_leave_it()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        Assert.Equal(SpeechSetupStep.Model, wizard.Step);
        Assert.False(wizard.IsModelReady);
        Assert.False(wizard.CanGoNext);
        Assert.False(wizard.CanGoBack);

        wizard.NextCommand.Execute(null);
        Assert.Equal(SpeechSetupStep.Model, wizard.Step);      // and it stayed put
    }

    /// <summary>Somebody re-running the wizard with a model already there walks straight through.</summary>
    [Fact]
    public void With_a_model_on_disk_the_first_step_is_already_satisfied()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        settings.Service.Settings.Speech.ModelId = "base";

        // Opening on a model that is on disk: the wizard picks it up and the step is satisfied.
        var reopened = new SpeechSetupViewModel(dictation, settings.Service);
        using var _r = reopened;

        Assert.True(reopened.IsModelReady);
        Assert.True(reopened.CanGoNext);

        reopened.NextCommand.Execute(null);
        Assert.Equal(SpeechSetupStep.Microphone, reopened.Step);

        reopened.NextCommand.Execute(null);
        Assert.Equal(SpeechSetupStep.Test, reopened.Step);
        Assert.Equal("Done", reopened.NextCaption);
    }

    /// <summary>
    /// Opening the wizard on a model it does not offer leaves that model alone.
    /// </summary>
    /// <remarks>
    /// <para>The list is the three <em>recommended</em> models of six. Somebody who picked Whisper Large
    /// in Settings and downloaded it has a working configuration that this list cannot represent — and
    /// the opening selection used to be written through the property, which writes the choice to
    /// settings. So the fallback to "the first row" replaced a model on disk with one that is not, and
    /// dictation stopped working because a window had been opened. Nothing else in the application
    /// changes a setting by being looked at.</para>
    /// <para>The chosen model is shown as a row of its own as well, and the first step is satisfied by
    /// what dictation would actually load rather than by what happens to be in the list.</para>
    /// </remarks>
    [Fact]
    public void Opening_it_does_not_replace_a_model_that_is_not_on_the_list()
    {
        PlaceOnDisk("medium-q5");
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.ModelId = "medium-q5";

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;
        using var wizard = new SpeechSetupViewModel(dictation, settings.Service);

        Assert.Equal("medium-q5", settings.Service.Settings.Speech.ModelId);
        Assert.Contains(wizard.Models, m => m.Id == "medium-q5");
        Assert.Equal("medium-q5", wizard.Selected?.Id);
        Assert.True(wizard.IsModelReady);
        Assert.True(wizard.CanGoNext);
    }

    /// <summary>
    /// Picking a model and then closing without downloading it does not take the working one away.
    /// </summary>
    /// <remarks>
    /// The other half of "opening this window changes nothing". Picking writes to settings straight away
    /// — that is what makes the list a choice rather than a form — and the first step then holds the
    /// user there until the file is on disk. Closing at that point left dictation pointed at a model
    /// that is not on the machine, which is the same broken configuration, arrived at one click later.
    /// Half-finished is not a configuration.
    /// </remarks>
    [Fact]
    public void Choosing_a_model_and_leaving_without_it_puts_the_working_one_back()
    {
        PlaceOnDisk("medium-q5");
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.ModelId = "medium-q5";

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;
        var wizard = new SpeechSetupViewModel(dictation, settings.Service);

        wizard.Selected = wizard.Models.First(m => m.Id == "parakeet-v3");   // nothing on disk for it
        Assert.Equal("parakeet-v3", settings.Service.Settings.Speech.ModelId);
        Assert.False(wizard.IsModelReady);

        wizard.Dispose();

        Assert.Equal("medium-q5", settings.Service.Settings.Speech.ModelId);
    }

    /// <summary>With nothing working to go back to, the choice stands — it is the only answer there is.</summary>
    [Fact]
    public void With_nothing_configured_the_choice_survives_closing()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;

        wizard.Selected = wizard.Models.First(m => m.Id == "small");
        wizard.Dispose();

        Assert.Equal("small", settings.Service.Settings.Speech.ModelId);
    }

    [Fact]
    public void Choosing_a_microphone_writes_it_to_settings()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Device = "Yeti";
        Assert.Equal("Yeti", settings.Service.Settings.Speech.InputDeviceName);

        wizard.Device = SettingsViewModel.DefaultDeviceOption;
        Assert.Equal("", settings.Service.Settings.Speech.InputDeviceName);
    }

    /// <summary>
    /// The test step records through the same service as everything else, and shows what came back.
    /// </summary>
    [Fact]
    public async Task The_last_step_records_and_shows_the_transcript()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        settings.Service.Settings.Speech.ModelId = "base";
        wizard.Step = SpeechSetupStep.Test;

        wizard.ToggleTestCommand.Execute(null);
        Assert.True(wizard.IsRecording);

        wizard.ToggleTestCommand.Execute(null);

        for (var i = 0; i < 100 && wizard.Transcript.Length == 0; i++)
            await Task.Delay(10);

        Assert.Equal("said something", wizard.Transcript);
        Assert.False(wizard.IsRecording);
    }

    /// <summary>
    /// Clicking Record while the previous sentence is still being worked out says so.
    /// </summary>
    /// <remarks>
    /// The same shape of bug as the tile's microphone button, and it was copied into the wizard after
    /// the tile had been fixed: `State != Idle` sends the click to Stop, which returns early when
    /// nothing is recording, so the button answers with nothing. Silence from a button is
    /// indistinguishable from a broken one — especially in a wizard whose whole point is showing that
    /// this works.
    /// </remarks>
    [Fact]
    public void Clicking_record_during_transcription_is_refused_out_loud()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        settings.Service.Settings.Speech.ModelId = "base";
        wizard.Step = SpeechSetupStep.Test;

        wizard.ToggleTestCommand.Execute(null);
        wizard.ToggleTestCommand.Execute(null);          // stop: now transcribing

        wizard.Message = null;
        wizard.ToggleTestCommand.Execute(null);          // and again, while it is still working

        Assert.NotNull(wizard.Message);
    }

    /// <summary>
    /// Closing the wizard mid-sentence gives the microphone back.
    /// </summary>
    /// <remarks>
    /// The service outlives this window by the life of the application. A recording left running has
    /// nowhere to deliver to and holds the device until its five-minute cap — the same failure a tile
    /// closing mid-recording used to have.
    /// </remarks>
    [Fact]
    public void Closing_it_while_recording_takes_the_microphone_back()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;

        settings.Service.Settings.Speech.ModelId = "base";
        wizard.Step = SpeechSetupStep.Test;
        wizard.ToggleTestCommand.Execute(null);
        Assert.Equal(DictationState.Recording, dictation.State);

        wizard.Dispose();

        Assert.Equal(DictationState.Idle, dictation.State);
        Assert.Null(dictation.Owner);
    }

    /// <summary>
    /// Closing the wizard stops a download it started.
    /// </summary>
    /// <remarks>
    /// The row holding the only reference to that download leaves the screen with the window, so half a
    /// gigabyte would go on arriving with no progress shown and no way to stop it. Nothing is lost by
    /// stopping it: the bytes stay in the <c>.partial</c> file and the next attempt resumes from them.
    /// </remarks>
    [Fact]
    public async Task Closing_it_stops_a_download_it_started()
    {
        var server = new NeverAnsweringServer();
        var (wizard, dictation, settings) = Build(new SpeechModelStore(_directory, () => new HttpClient(server)));
        using var _ = settings;
        using var _d = dictation;

        var row = wizard.Models[0];
        var download = row.DownloadCommand.ExecuteAsync(null);
        await server.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(row.IsDownloading);

        wizard.Dispose();
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(row.IsDownloading);
        Assert.Null(row.Error);                 // cancelling is not a failure to report
    }

    // ---- the shortcut, taught on the last step by being used ----

    /// <summary>
    /// The step shows the configured shortcut as keys to press.
    /// </summary>
    /// <remarks>
    /// There is no separate page for the shortcut, and this is why: the last step already asks somebody
    /// to make dictation happen, so it may as well ask with the keys they will use every day. A page
    /// that only let them type a combination and click Next would prove nothing about it.
    /// </remarks>
    [Fact]
    public void The_last_step_shows_the_shortcut_as_keys()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);
        Assert.True(wizard.HasHotkey);
        Assert.Equal(["Alt", "Space"], wizard.HotkeyKeys);
        Assert.Null(wizard.HotkeyWarning);
    }

    /// <summary>
    /// The instruction says what the configured mode actually does.
    /// </summary>
    /// <remarks>
    /// It said "Hold" to everybody, and in toggle mode the machine ignores the release — so somebody
    /// following it held the keys, spoke, let go, and the recording ran on to its five-minute cap with
    /// "Listening…" on screen and no transcript ever arriving. The step that exists to prove dictation
    /// works was proving the opposite, in its own words. The mode is set in Settings and this window does
    /// not offer it, which is precisely why the wording cannot assume it.
    /// </remarks>
    [Theory]
    [InlineData(DictationMode.PushToTalk, "Hold", false)]
    [InlineData(DictationMode.Toggle, "Press", true)]
    public void The_instruction_matches_the_mode(DictationMode mode, string verb, bool needsFollowUp)
    {
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.Mode = mode;

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;
        using var wizard = new SpeechSetupViewModel(dictation, settings.Service);

        Assert.Equal(mode == DictationMode.PushToTalk, wizard.IsPushToTalk);
        Assert.Equal(verb, wizard.HotkeyVerb);

        // Toggle needs the second half said out loud: nothing else on the page tells anyone that
        // letting go will not end the recording.
        Assert.Equal(needsFollowUp, !string.IsNullOrEmpty(wizard.HotkeyFollowUp));
    }

    /// <summary>Choosing different keys writes them, and the mode ends with the keystroke that answered
    /// it.</summary>
    [Fact]
    public void Capturing_a_shortcut_writes_it_and_ends_the_mode()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.BeginCaptureHotkeyCommand.Execute(null);
        Assert.True(wizard.IsCapturingHotkey);

        Assert.True(wizard.CaptureHotkey(Key.D, KeyModifiers.Control | KeyModifiers.Shift));

        Assert.False(wizard.IsCapturingHotkey);
        Assert.Equal("Ctrl+Shift+D", settings.Service.Settings.Speech.Hotkey);
        Assert.Equal(["Ctrl", "Shift", "D"], wizard.HotkeyKeys);
    }

    /// <summary>
    /// Reaching for a combination does not bind the modifier on the way.
    /// </summary>
    /// <remarks>
    /// The keystrokes that make up <c>Ctrl+Shift+D</c> arrive as four separate events, three of them
    /// modifiers. Acting on the first would store "Ctrl" and drop the user out of the mode before they
    /// had finished pressing what they meant — and the whole point of a mode that ends by itself is that
    /// it ends on the <em>answer</em>.
    /// </remarks>
    [Fact]
    public void A_modifier_on_its_own_does_not_end_the_capture()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.BeginCaptureHotkeyCommand.Execute(null);

        Assert.False(wizard.CaptureHotkey(Key.LeftCtrl, KeyModifiers.Control));
        Assert.True(wizard.IsCapturingHotkey);
        Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);
    }

    /// <summary>
    /// Escape is not an answer here, so the window keeps it.
    /// </summary>
    /// <remarks>
    /// It has to mean "leave it as it was", and it can only mean that if this refuses it — the window is
    /// what turns the refusal into cancelling the mode. Bound instead, it would give the key that
    /// cancels a recording the job of starting one.
    /// </remarks>
    [Fact]
    public void Escape_is_not_captured()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.BeginCaptureHotkeyCommand.Execute(null);

        Assert.False(wizard.CaptureHotkey(Key.Escape, KeyModifiers.None));
        Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);

        wizard.CancelCaptureHotkeyCommand.Execute(null);
        Assert.False(wizard.IsCapturingHotkey);
    }

    /// <summary>
    /// Leaving the step takes the capture mode with it.
    /// </summary>
    /// <remarks>
    /// <para>Reachable, and quiet if it is wrong. "Use different keys" only appears on the last step, but
    /// <b>Back</b> is in the footer on every one — and the window checks <c>IsCapturingHotkey</c> before
    /// it checks which step is showing, because a capture has to swallow whatever is pressed. So a
    /// capture that survived the step change would leave the user on the microphone page with the next
    /// key they touched silently becoming their shortcut: the arrow that was meant to walk the device
    /// list is bound as <c>Down</c> and eaten, and nothing on screen says why the list will not move.</para>
    /// <para>Asserted by trying the keystroke rather than by reading the flag, because the flag is not the
    /// point — what it does to the next key is.</para>
    /// </remarks>
    [Fact]
    public void Leaving_the_step_stops_the_next_key_becoming_a_shortcut()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;
        wizard.BeginCaptureHotkeyCommand.Execute(null);
        Assert.True(wizard.IsCapturingHotkey);

        wizard.BackCommand.Execute(null);
        Assert.Equal(SpeechSetupStep.Microphone, wizard.Step);
        Assert.False(wizard.IsCapturingHotkey);

        Assert.False(wizard.CaptureHotkey(Key.Down, KeyModifiers.None));
        Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);
    }

    /// <summary>Backspace in the capture, and the button beside it, both mean "no shortcut" — which is a
    /// real answer, not a failure to give one, and the step says so rather than warning about it.</summary>
    [Fact]
    public void The_shortcut_can_be_turned_off()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.BeginCaptureHotkeyCommand.Execute(null);
        Assert.True(wizard.CaptureHotkey(Key.Back, KeyModifiers.None));

        Assert.Equal("", settings.Service.Settings.Speech.Hotkey);
        Assert.False(wizard.HasHotkey);
        Assert.Empty(wizard.HotkeyKeys);

        wizard.CaptureHotkey(Key.Space, KeyModifiers.Alt);      // not capturing any more: ignored
        Assert.Equal("", settings.Service.Settings.Speech.Hotkey);

        wizard.ClearHotkeyCommand.Execute(null);
        Assert.Equal("", settings.Service.Settings.Speech.Hotkey);

        // The other half of the pair below: nothing there because that is what was asked for.
        Assert.False(wizard.HasHotkey);
        Assert.True(wizard.IsShortcutBlank);
        Assert.Null(wizard.HotkeyWarning);
    }

    /// <summary>
    /// A shortcut that cannot be listened for is not the same as no shortcut.
    /// </summary>
    /// <remarks>
    /// A hand-edited settings file, or one from a version that spelled things differently. The step
    /// answered both with "No shortcut — dictation runs from the microphone button", so the user was told
    /// they had made a choice they had not made, with nothing to suggest anything was wrong — while the
    /// Speech tab, looking at the same setting, said it was unusable. Two windows disagreeing about one
    /// value is the failure this feature keeps having to guard against.
    /// </remarks>
    [Fact]
    public void An_unusable_shortcut_is_not_reported_as_having_none()
    {
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.Hotkey = "Alt+9999";

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;
        using var wizard = new SpeechSetupViewModel(dictation, settings.Service);

        Assert.False(wizard.HasHotkey);              // there are no keys to show
        Assert.False(wizard.IsShortcutBlank);        // but the user did not ask for none
        Assert.Equal(HotkeyAdvice.Unparseable, wizard.HotkeyWarning);
    }

    /// <summary>
    /// Choosing a new shortcut ends a recording rather than running alongside it.
    /// </summary>
    /// <remarks>
    /// The two modes contradict each other on screen — "Listening…" beside "Press the keys you want" —
    /// and in the keyboard: every key pressed to end the recording was bound as a shortcut instead,
    /// leaving it running to its five-minute cap. Reachable with the Record button, and in toggle mode
    /// with the shortcut itself.
    /// </remarks>
    [Fact]
    public void Choosing_a_new_shortcut_ends_a_recording_first()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        settings.Service.Settings.Speech.ModelId = "base";
        wizard.Step = SpeechSetupStep.Test;

        wizard.ToggleTestCommand.Execute(null);
        Assert.True(wizard.IsRecordingHere);

        wizard.BeginCaptureHotkeyCommand.Execute(null);

        Assert.False(wizard.IsRecordingHere);
        Assert.True(wizard.IsCapturingHotkey);
    }

    /// <summary>A bare key is bound, and said out loud — the same sentence the Speech tab shows.</summary>
    [Fact]
    public void A_shortcut_with_no_modifier_is_allowed_and_explained()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.BeginCaptureHotkeyCommand.Execute(null);
        wizard.CaptureHotkey(Key.F13, KeyModifiers.None);

        Assert.Equal("F13", settings.Service.Settings.Speech.Hotkey);
        Assert.NotNull(wizard.HotkeyWarning);
    }

    /// <summary>
    /// Closing the wizard keeps the shortcut, unlike the model.
    /// </summary>
    /// <remarks>
    /// The model is put back because choosing one can be left half-done — picked but not downloaded —
    /// and closing on that leaves dictation pointing at a file that is not on the machine. A captured
    /// gesture has no half-done state: it is usable the moment it is pressed. Pinned because the
    /// symmetry with the step above it is exactly the kind that gets "tidied up" into a restore that
    /// silently undoes what the user just chose.
    /// </remarks>
    [Fact]
    public void Closing_the_wizard_keeps_the_shortcut_that_was_chosen()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;

        wizard.BeginCaptureHotkeyCommand.Execute(null);
        wizard.CaptureHotkey(Key.D, KeyModifiers.Control | KeyModifiers.Alt);

        wizard.Dispose();

        Assert.Equal("Ctrl+Alt+D", settings.Service.Settings.Speech.Hotkey);
    }

    /// <summary>
    /// A shortcut that arrives from somewhere other than the Speech tab's own box is still explained.
    /// </summary>
    /// <remarks>
    /// The tab worked its warning out in the property setter, which runs when somebody types in the box —
    /// and both the settings file and the setup wizard reach the tab by writing the backing field, so
    /// neither saves everything back. A bare key set in the wizard was therefore accepted in silence, and
    /// a warning from before it stayed up afterwards. Asked of the constructor because the wizard's
    /// return runs the same <c>InitializeSpeech</c>.
    /// </remarks>
    [Theory]
    // One of each: which kinds warn is settled exhaustively, and without a view model, in
    // HotkeyCaptureTests. What is asked here is only whether the tab shows what HotkeyAdvice said.
    [InlineData("F13", true)]
    [InlineData("Alt+Space", false)]
    public void The_speech_tab_explains_a_shortcut_it_did_not_type(string stored, bool warns)
    {
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.Hotkey = stored;

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;

        var tab = new SettingsViewModel(settings.Service, dictation: dictation);

        Assert.Equal(stored, tab.SpeechHotkey);
        Assert.Equal(warns, !string.IsNullOrEmpty(tab.SpeechHotkeyWarning));
    }

    /// <summary>
    /// With the feature switched off, the step says so and offers the switch.
    /// </summary>
    /// <remarks>
    /// <para>A dead end made of two correct components. <b>Set up dictation…</b> is not gated on the
    /// switch — configuring before enabling is a reasonable order to work in — so somebody who has turned
    /// dictation off reaches a page reading "Hold Alt+Space and say something", an instruction that cannot
    /// succeed. What came back was the service's own refusal, pointing them at Settings → Speech: the
    /// window this modal is covering.</para>
    /// <para>The hint stays down for the same reason a refused press still counts as the keys arriving —
    /// telling somebody the desktop has taken their shortcut, when dictation is simply off, sends them to
    /// fix the wrong thing.</para>
    /// </remarks>
    [Fact]
    public void With_dictation_switched_off_the_step_offers_the_switch()
    {
        PlaceOnDisk("base");
        var schedule = new ManualSchedule();
        var settings = new TempSettings();
        using var _ = settings;
        settings.Service.Settings.Speech.ModelId = "base";
        settings.Service.Settings.Speech.Enabled = false;

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        using var _d = dictation;
        using var wizard = new SpeechSetupViewModel(dictation, settings.Service, schedule.Schedule);

        Assert.True(wizard.IsDictationOff);

        wizard.Step = SpeechSetupStep.Test;
        Assert.False(wizard.StartTest());              // the instruction on the page cannot succeed

        // And it is not shown. "Hold Alt Space and say something" directly under "nothing here will
        // record" is an instruction and its own refutation, one line apart.
        Assert.True(wizard.HasHotkey);
        Assert.False(wizard.ShowsShortcutInstruction);

        schedule.Elapse();
        Assert.False(wizard.ShowHotkeyHint);           // and the reason is not the desktop taking keys

        wizard.TurnDictationOnCommand.Execute(null);

        Assert.False(wizard.IsDictationOff);
        Assert.True(settings.Service.Settings.Speech.Enabled);
        Assert.True(wizard.ShowsShortcutInstruction);  // and the instruction comes back with it
        Assert.True(wizard.StartTest());               // fixed in place, without leaving the window
    }

    // ---- the hint for the failure with nothing on screen ----

    /// <summary>
    /// Holding the keys and getting nothing is the one failure this step cannot otherwise report.
    /// </summary>
    [Fact]
    public void A_shortcut_that_never_arrives_is_eventually_explained()
    {
        var schedule = new ManualSchedule();
        var (wizard, dictation, settings) = Build(schedule: schedule);
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        Assert.False(wizard.ShowHotkeyHint);
        Assert.Equal(0, schedule.Scheduled);         // nothing is waiting before the step is reached

        wizard.Step = SpeechSetupStep.Test;
        Assert.False(wizard.ShowHotkeyHint);         // and not before the wait is up
        schedule.Elapse();

        Assert.True(wizard.ShowHotkeyHint);
    }

    /// <summary>
    /// A shortcut that does arrive answers the hint, whether or not the recording then starts.
    /// </summary>
    /// <remarks>
    /// The hint is about the keys reaching the application and nothing else. A press refused for want of
    /// a model has already answered it — telling somebody their shortcut may be taken by the desktop,
    /// when it plainly is not, sends them to fix the wrong thing.
    /// </remarks>
    [Fact]
    public void A_shortcut_that_does_arrive_answers_the_hint()
    {
        var schedule = new ManualSchedule();
        var (wizard, dictation, settings) = Build(schedule: schedule);
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;
        wizard.NoteHotkeyPressed();
        schedule.Elapse();

        Assert.False(wizard.ShowHotkeyHint);
    }

    /// <summary>Nor does it arrive over a step the user has already left.</summary>
    [Fact]
    public void The_hint_does_not_follow_the_user_off_the_step()
    {
        var schedule = new ManualSchedule();
        var (wizard, dictation, settings) = Build(schedule: schedule);
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;
        wizard.Step = SpeechSetupStep.Microphone;
        schedule.Elapse();

        Assert.False(wizard.ShowHotkeyHint);
    }

    /// <summary>
    /// With no shortcut there is nothing to hold, so there is nothing to explain.
    /// </summary>
    /// <remarks>
    /// Somebody who has just turned the shortcut off would otherwise be told, twelve seconds later, that
    /// their shortcut might be taken by the desktop.
    /// </remarks>
    [Fact]
    public void Turning_the_shortcut_off_takes_the_hint_with_it()
    {
        var schedule = new ManualSchedule();
        var (wizard, dictation, settings) = Build(schedule: schedule);
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;
        wizard.ClearHotkeyCommand.Execute(null);
        schedule.Elapse();

        Assert.False(wizard.ShowHotkeyHint);
    }

    /// <summary>
    /// The shortcut and the button start the same recording.
    /// </summary>
    /// <remarks>
    /// The button is the fallback for a shortcut the desktop has taken, so the two must not drift into
    /// two slightly different trials — which is why the window's own push-to-talk machine calls the same
    /// pair of methods the button does.
    /// </remarks>
    [Fact]
    public void The_shortcut_and_the_button_run_the_same_trial()
    {
        PlaceOnDisk("base");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        settings.Service.Settings.Speech.ModelId = "base";
        wizard.Step = SpeechSetupStep.Test;

        Assert.True(wizard.StartTest());                 // what the held shortcut does
        Assert.True(wizard.IsRecordingHere);
        Assert.Equal(DictationState.Recording, dictation.State);

        wizard.StopTest();
        Assert.NotEqual(DictationState.Recording, dictation.State);
    }

    /// <summary>
    /// A refused start says so rather than leaving the shortcut looking dead.
    /// </summary>
    /// <remarks>
    /// <para>The window resets its push-to-talk machine on a false return; without that the machine goes
    /// on believing it is recording, and the next press is read as the one that stops it — so the
    /// shortcut answers with nothing at all, once, at random.</para>
    /// <para>And the refusal is shown in the wizard's own window. The application's handler for these
    /// opens a box owned by the main window; while the wizard is modal that box is unreachable, so the
    /// failure would look like nothing happening at all. The button goes the same way — with nothing
    /// recording, <c>ToggleTest</c> falls straight through to this — which is pinned by
    /// <see cref="The_shortcut_and_the_button_run_the_same_trial"/>.</para>
    /// </remarks>
    [Fact]
    public void A_refused_start_is_reported_rather_than_swallowed()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;

        Assert.False(wizard.StartTest());                // no model on disk
        Assert.NotNull(wizard.Message);
        Assert.False(wizard.IsRecordingHere);
        Assert.Equal(DictationState.Idle, dictation.State);
    }

    /// <summary>
    /// Running the wizard from Settings does not re-arm the first-run question.
    /// </summary>
    /// <remarks>
    /// The flag records that the question has been put, and running the wizard <em>is</em> putting it.
    /// Clearing it would mean a prompt at the next launch for somebody who has just been through setup.
    /// </remarks>
    [Fact]
    public void Running_the_wizard_does_not_bring_the_first_run_prompt_back()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        dictation.MarkModelPromptAnswered();
        Assert.True(settings.Service.Settings.Speech.ModelPromptAnswered);

        wizard.Device = "Yeti";
        wizard.CloseCommand.Execute(null);

        Assert.True(settings.Service.Settings.Speech.ModelPromptAnswered);
        Assert.False(dictation.ShouldOfferModelDownload());
    }
}
