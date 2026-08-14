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

    private (SpeechSetupViewModel Wizard, DictationService Dictation, TempSettings Settings) Build(
        SpeechModelStore? store = null)
    {
        var settings = new TempSettings();
        var dictation = new DictationService(settings.Service, new FakeCapture(), new SilentEngine(),
            store ?? new SpeechModelStore(_directory), action => action());

        return (new SpeechSetupViewModel(dictation, settings.Service), dictation, settings);
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

    /// <summary>Choosing a model in the wizard is choosing it for the application.</summary>
    [Fact]
    public void Picking_a_model_writes_it_to_settings()
    {
        PlaceOnDisk("small");
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Selected = wizard.Models.First(m => m.Id == "small");

        Assert.Equal("small", settings.Service.Settings.Speech.ModelId);
        Assert.True(wizard.IsModelReady);
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
    /// Failures reach the wizard's own window rather than a message box behind it.
    /// </summary>
    /// <remarks>
    /// The application's handler for these opens a box owned by the main window; while the wizard is
    /// modal that box is unreachable, so the failure would look like nothing happening at all.
    /// </remarks>
    [Fact]
    public void A_failure_is_shown_in_the_wizard()
    {
        var (wizard, dictation, settings) = Build();
        using var _ = settings;
        using var _d = dictation;
        using var _w = wizard;

        wizard.Step = SpeechSetupStep.Test;
        wizard.ToggleTestCommand.Execute(null);          // no model on disk: refused

        Assert.NotNull(wizard.Message);
        Assert.Equal(DictationState.Idle, dictation.State);
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
