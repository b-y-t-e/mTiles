using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the dictation shortcut does to the wizard window it is pressed in.
/// </summary>
/// <remarks>
/// <para>The last step asks the user to hold <c>Alt+Space</c>. Nobody reaches it except by clicking
/// <b>Next</b>, so the footer button has the focus — and a focused <see cref="Button"/> raises its Click
/// from the key-<em>up</em> of Space, regardless of the key-down having been marked handled. On the last
/// step that button says <b>Done</b>. Letting go of the shortcut therefore shut the whole wizard, on the
/// first attempt anybody ever made to use dictation, at the exact moment the transcript was about to
/// arrive.</para>
/// <para>It needed a real window to find, which is why it was found by a user rather than by the twenty
/// tests around it: every rule involved is right on its own, and the failure is in how two of them meet
/// in Avalonia's routing. These run headless for that reason — the point is the routing, not the
/// pixels.</para>
/// </remarks>
public class SpeechSetupWizardKeyTests : IDisposable
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

    public SpeechSetupWizardKeyTests()
    {
        Directory.CreateDirectory(_directory);

        // A model on disk, so a held shortcut has something to start. Without it the recording is
        // refused and the test could pass by the gesture doing nothing at all.
        var model = SpeechModelCatalog.Find("base")!;
        using var file = File.Create(Path.Combine(_directory, model.FileName));
        file.SetLength(model.DownloadBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SpeechSetupWizardKeyTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>The wizard as the user meets it on the last step: shown, and with the focus still on the
    /// button they clicked to get there.</summary>
    private (SpeechSetupWizard Window, SpeechSetupViewModel Model, DictationService Dictation,
        TempSettings Settings, Func<int> Closes) OnTheLastStep()
    {
        var settings = new TempSettings();
        settings.Service.Settings.Speech.ModelId = "base";

        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            new SpeechModelStore(_directory), action => action());
        var model = new SpeechSetupViewModel(dictation, settings.Service);

        var closes = 0;
        model.CloseRequested += () => closes++;

        var window = new SpeechSetupWizard { DataContext = model };
        window.Bind(model, dictation, settings.Service);
        window.Show();

        model.Step = SpeechSetupStep.Test;
        Assert.Equal("Done", model.NextCaption);

        var next = window.FindControl<Button>("NextButton");
        Assert.NotNull(next);
        next.Focus();

        return (window, model, dictation, settings, () => closes);
    }

    /// <summary>
    /// Holding and releasing the shortcut records — and does not press the button behind it.
    /// </summary>
    [Fact]
    public void Letting_go_of_the_shortcut_does_not_close_the_wizard()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            Assert.True(model.IsRecordingHere);          // the gesture really did reach the machine

            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.Equal(0, closes());
        });

    /// <summary>
    /// And the same keys pressed while choosing a new shortcut do not press it either.
    /// </summary>
    /// <remarks>
    /// The other way in: "use different keys", then bind <c>Alt+Space</c>. The press is swallowed by the
    /// capture, and the release used to reach the focused Done button exactly as above — so the wizard
    /// closed on the keystroke that was meant to configure it.
    /// </remarks>
    [Fact]
    public void Binding_a_shortcut_that_uses_space_does_not_close_the_wizard()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            model.BeginCaptureHotkeyCommand.Execute(null);

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.False(model.IsCapturingHotkey);
            Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);
            Assert.Equal(0, closes());
        });

    /// <summary>
    /// A bare Space is not the shortcut, so it presses the button — even with <c>Alt+Space</c> bound.
    /// </summary>
    /// <remarks>
    /// <para>The counterweight the first version of this file was missing. It cleared the shortcut before
    /// pressing Space, which meant the fix could be "swallow every release of the gesture's key" and still
    /// pass — and that fix shipped. With <c>Alt+Space</c> bound, the press of a bare Space is correctly
    /// left alone, and swallowing its release stopped the focused button firing at all: somebody with a
    /// Space shortcut could not press <b>Done</b>, or anything else, from the keyboard.</para>
    /// <para>What a release owes is symmetry with its own press, not with the key. A test that removes
    /// the interesting state before asking the question cannot tell the two rules apart.</para>
    /// </remarks>
    [Fact]
    public void A_bare_space_still_presses_the_button_while_the_shortcut_uses_space()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            Assert.Equal("Alt+Space", settings.Service.Settings.Speech.Hotkey);

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.False(model.IsRecordingHere);        // it was never the shortcut
            Assert.Equal(1, closes());
        });

    /// <summary>
    /// And with no shortcut at all, nothing about Space changes either.
    /// </summary>
    [Fact]
    public void Space_still_presses_the_button_when_there_is_no_shortcut()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            model.ClearHotkeyCommand.Execute(null);
            Assert.False(model.HasHotkey);

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.Equal(1, closes());
        });

    /// <summary>
    /// Walking away from the step mid-sentence gives the microphone back.
    /// </summary>
    /// <remarks>
    /// <para>Hold the shortcut, click <b>Back</b> with the other hand. The step change moves the page out
    /// from under the recording: "Listening…" belongs to the step that has just gone, so the microphone
    /// stayed open with <em>nothing anywhere on screen</em> to say so until the service's own five-minute
    /// cap closed it.</para>
    /// <para>It was two failures at once. The release was gated on the step, so the machine never learnt
    /// the key had come up and went on believing a push-to-talk was in progress; and nothing ended the
    /// recording when the step changed. Both are fixed, and this exercises the pair through a real window
    /// because that is the only place the two meet.</para>
    /// </remarks>
    [Fact]
    public void Leaving_the_step_while_holding_the_shortcut_gives_the_microphone_back()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, _) = OnTheLastStep();
            using var __ = settings;
            using var _d = dictation;

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            Assert.True(model.IsRecordingHere);

            model.BackCommand.Execute(null);
            Assert.Equal(SpeechSetupStep.Microphone, model.Step);

            Assert.Equal(DictationState.Idle, dictation.State);
            Assert.Null(dictation.Owner);

            // And the shortcut is not left half-pressed. The release lands while the microphone step is
            // showing, so it only reaches the machine because the release is no longer gated on the step.
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            // Past the machine's 30 ms debounce and well inside its 1 s "still held" window — which is
            // what makes the press below tell the two apart. Unheard, the release leaves the key believed
            // down and the press is dropped as auto-repeat; heard, it starts a recording. Without the
            // wait the debounce swallows it either way and the assertion proves nothing.
            Thread.Sleep(60);

            model.Step = SpeechSetupStep.Test;
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            Assert.True(model.IsRecordingHere);
        });

    /// <summary>
    /// Escape during a recording does not hand the shortcut's release to the button.
    /// </summary>
    /// <remarks>
    /// Two claimed presses overlap here, and a single-slot claim let the second overwrite the first: hold
    /// <c>Alt+Space</c> — Space claimed, recording — then press <b>Escape</b> to abandon it, which this
    /// handler also takes. The claim became Escape, so letting go of Space was no longer ours, reached the
    /// focused <b>Done</b> button and shut the wizard. The originally reported bug, arrived at down a
    /// different path.
    /// </remarks>
    [Fact]
    public void Escaping_a_recording_does_not_hand_the_release_to_the_button()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            Assert.True(model.IsRecordingHere);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.Alt);
            Assert.False(model.IsRecordingHere);            // Escape threw the recording away
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.Alt);

            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.Equal(0, closes());
        });

    /// <summary>
    /// Binding a new shortcut while the old one is held does not either.
    /// </summary>
    /// <remarks>The same overlap through the capture: the press that binds is claimed too, and with one
    /// slot it displaced the claim on the key still physically down.</remarks>
    [Fact]
    public void Binding_while_the_old_shortcut_is_held_does_not_close_the_wizard()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);
            Assert.True(model.IsRecordingHere);

            model.BeginCaptureHotkeyCommand.Execute(null);   // ends the recording, waits for keys
            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.D, RawInputModifiers.None);
            Assert.Equal("Ctrl+D", settings.Service.Settings.Speech.Hotkey);

            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.Equal(0, closes());
        });

    /// <summary>
    /// A settings listener that throws does not come back out of the keyboard handler.
    /// </summary>
    /// <remarks>
    /// <para>Binding a shortcut writes it to settings, and that raises <c>SettingsChanged</c> to whoever
    /// is listening — the theme bridge, the database tile, the main view model. A fault in any one of them
    /// would otherwise travel up out of a key handler, which is the same path <c>DictationHotkeys</c> has
    /// always guarded and this window did not.</para>
    /// <para>The application survives it either way — <c>CrashHandler</c> marks dispatcher exceptions
    /// handled — so this is about the failure being reported as what it is, and about the two copies of
    /// one handler shape not disagreeing on it.</para>
    /// </remarks>
    [Fact]
    public void A_settings_listener_that_throws_does_not_escape_the_key_handler()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, _) = OnTheLastStep();
            using var __ = settings;
            using var _d = dictation;

            settings.Service.SettingsChanged += () => throw new InvalidOperationException("a listener");

            model.BeginCaptureHotkeyCommand.Execute(null);

            // Would propagate out of RaiseEvent and fail the test without the guard.
            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.D, RawInputModifiers.None);
        });

    /// <summary>
    /// A release the window never saw does not leave the key swallowed for good.
    /// </summary>
    /// <remarks>
    /// Alt+Tab away mid-hold and the key-up happens somewhere else. The claim on that key is settled by
    /// the next press of it rather than left standing, or Space would quietly stop working on every
    /// button in the window with nothing on screen to say why.
    /// </remarks>
    [Fact]
    public void A_release_that_never_arrived_does_not_swallow_the_key_for_ever()
        => OnUiThread(() =>
        {
            var (window, model, dictation, settings, closes) = OnTheLastStep();
            using var _ = settings;
            using var _d = dictation;

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Alt);   // held, and never released here
            Assert.True(model.IsRecordingHere);
            dictation.Cancel();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

            Assert.Equal(1, closes());
        });
}
