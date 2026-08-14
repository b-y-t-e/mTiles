using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The whole dictation flow with a fake microphone and a fake engine: no audio device, no model file,
/// no dispatcher. What is being checked is the part a user notices — that nothing is delivered when it
/// should not be, and that the tile it was started for is the one that gets the text.
/// </summary>
public class DictationServiceTests
{
    private sealed class FakeCapture : IAudioCapture
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsRecording { get; private set; }
        public float[] Samples { get; set; } = new float[16_000];
        public int StopCount { get; private set; }

        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["fake microphone"];

        /// <summary>Set to make the next start fail, as a device that is busy or gone would.</summary>
        public bool FailNextStart { get; set; }

        public void Start(string deviceName)
        {
            // The real capture refuses a second stream rather than replacing one, and the service must
            // never ask it to: a start while the previous recording is still attached is the bug this
            // fake exists to catch.
            Assert.False(IsRecording, "started a recording while the previous one was still attached");

            if (FailNextStart)
            {
                FailNextStart = false;
                // And the real one leaves nothing behind when this happens: the device is released and
                // IsRecording stays false, so the next attempt is allowed to try again.
                throw new InvalidOperationException("the device could not be opened");
            }

            IsRecording = true;
        }

        /// <summary>What Detach hands back: this fake's whole recording is the samples it will return.</summary>
        private sealed record Handle(float[] Samples) : IRecordingHandle;

        /// <summary>The two halves, as the real one has them: detaching is instant, finishing is not.</summary>
        public IRecordingHandle? Detach()
        {
            if (!IsRecording)
                return null;

            IsRecording = false;
            return new Handle(Samples);
        }

        public float[] Finish(IRecordingHandle? detached)
        {
            if (detached is not Handle handle)
                return [];

            StopCount++;
            return handle.Samples;
        }

        public void Dispose() { }
    }

    private sealed class FakeEngine : ISpeechToTextEngine
    {
        private readonly TaskCompletionSource _release = new();

        public bool IsLoaded { get; private set; }
        public string Transcript { get; set; } = "  hello   world  ";
        public bool BlockUntilReleased { get; set; }
        public int Calls { get; private set; }

        public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
        {
            IsLoaded = true;
            return Task.CompletedTask;
        }

        public void Unload() => IsLoaded = false;

        public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (BlockUntilReleased)
                await _release.Task.WaitAsync(cancellationToken);
            return Transcript;
        }

        public void Release() => _release.TrySetResult();

        public void Dispose() { }
    }

    /// <summary>A model store pointed at a directory holding a file of exactly the right size, so the
    /// service believes the model is downloaded without any hundreds of megabytes being involved.</summary>
    private sealed class FakeModels : IDisposable
    {
        /// <summary>A single-file model, so the fixture is one `SetLength` rather than an archive.</summary>
        private static readonly SpeechModel Model = SpeechModelCatalog.Find("base")!;

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

        public FakeModels(bool downloaded = true)
        {
            Directory.CreateDirectory(_directory);
            Store = new SpeechModelStore(_directory);
            if (!downloaded)
                return;

            using var file = File.Create(Path.Combine(_directory, Model.FileName));
            file.SetLength(Model.DownloadBytes);
        }

        public SpeechModelStore Store { get; }

        public string SelectedModelId => Model.Id;

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private static (DictationService Service, FakeCapture Capture, FakeEngine Engine) Build(
        TempSettings settings, FakeModels models, bool enabled = true,
        TimeSpan? maxRecording = null, TimeSpan? unloadAfter = null)
    {
        var speech = settings.Service.Settings.Speech;
        speech.Enabled = enabled;
        speech.ModelId = models.SelectedModelId;

        var capture = new FakeCapture();
        var engine = new FakeEngine();
        // Dispatch inline: there is no UI thread here, and the ordering the tests rely on is the
        // service's own, not the dispatcher's.
        var service = new DictationService(settings.Service, capture, engine, models.Store, action => action(),
            maxRecording, unloadAfter);
        return (service, capture, engine);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        for (var i = 0; i < timeoutMs / 10 && !condition(); i++)
            await Task.Delay(10);
        return condition();
    }

    /// <summary>
    /// A recording nobody stops is stopped anyway, and what was said is kept.
    /// </summary>
    /// <remarks>
    /// Push-to-talk ends when the key comes up; toggle mode ends only when somebody presses again, and
    /// a recording left running grows at 64 KB a second towards a transcription of an hour of audio.
    /// The cap transcribes rather than discards — the words already spoken are still worth having.
    /// </remarks>
    [Fact]
    public async Task A_recording_nobody_stops_is_cut_off_and_still_delivered()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, _) = Build(settings, models, maxRecording: TimeSpan.FromMilliseconds(150));
        using var _guard = service;

        string? delivered = null;
        Assert.True(service.Start(new object(), text => { delivered = text; return true; }));

        Assert.Equal("hello world", await WaitForDeliveryAsync(() => delivered));
        Assert.Equal(DictationState.Idle, service.State);
    }

    /// <summary>
    /// The model is dropped after the idle period — hundreds of megabytes of resident memory that a
    /// terminal manager has no business holding between one prompt and the next.
    /// </summary>
    [Fact]
    public async Task The_model_is_unloaded_once_dictation_has_been_idle()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, engine) = Build(settings, models, unloadAfter: TimeSpan.FromMilliseconds(100));
        using var _guard = service;

        Assert.True(service.Start(new object(), _ => true));
        service.Stop();
        Assert.True(await WaitUntilAsync(() => engine.IsLoaded), "the model was never loaded");

        Assert.True(await WaitUntilAsync(() => !engine.IsLoaded), "the model was never unloaded");
    }

    /// <summary>Zero minutes means never: worth offering, because reloading Parakeet costs two seconds
    /// and which of the two matters is the user's to weigh.</summary>
    [Fact]
    public async Task An_unload_period_of_zero_keeps_the_model_loaded()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        settings.Service.Settings.Speech.ModelUnloadMinutes = 0;
        var (service, _, engine) = Build(settings, models, unloadAfter: TimeSpan.FromMilliseconds(50));
        using var _guard = service;

        Assert.True(service.Start(new object(), _ => true));
        service.Stop();
        Assert.True(await WaitUntilAsync(() => engine.IsLoaded), "the model was never loaded");

        await Task.Delay(250);
        Assert.True(engine.IsLoaded);
    }

    private static async Task<string?> WaitForDeliveryAsync(Func<string?> read)
    {
        for (var i = 0; i < 200 && read() is null; i++)
            await Task.Delay(10);
        return read();
    }

    [Fact]
    public async Task A_recording_is_transcribed_cleaned_and_delivered_to_its_owner()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, _) = Build(settings, models);
        using var _guard = service;

        var owner = new object();
        string? delivered = null;

        Assert.True(service.Start(owner, text => { delivered = text; return true; }));
        Assert.Equal(DictationState.Recording, service.State);
        Assert.Same(owner, service.Owner);

        service.Stop();
        Assert.Equal("hello world", await WaitForDeliveryAsync(() => delivered));
        Assert.Equal(DictationState.Idle, service.State);
        Assert.Null(service.Owner);
    }

    [Fact]
    public void It_refuses_to_start_when_dictation_is_switched_off()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, _) = Build(settings, models, enabled: false);
        using var _guard = service;

        string? error = null;
        service.Error += message => error = message;

        Assert.False(service.Start(new object(), _ => true));
        Assert.False(capture.IsRecording);
        Assert.NotNull(error);
    }

    [Fact]
    public void It_refuses_to_start_when_the_model_has_not_been_downloaded()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels(downloaded: false);
        var (service, capture, _) = Build(settings, models);
        using var _guard = service;

        string? error = null;
        service.Error += message => error = message;

        Assert.False(service.Start(new object(), _ => true));
        Assert.False(capture.IsRecording);
        Assert.NotNull(error);
    }

    /// <summary>
    /// A microphone that refuses to open is reported and then forgotten about — the next attempt is a
    /// fresh one.
    /// </summary>
    /// <remarks>
    /// The device can be busy, or gone between the moment it was listed and the moment it is opened.
    /// What must not survive that is any trace of the attempt: the real capture used to keep the failed
    /// stream in its field, which made it believe it was recording for ever, and since there was no
    /// recording to detach, every later press was refused. One busy microphone and dictation was dead
    /// until the application restarted.
    /// </remarks>
    [Fact]
    public async Task A_microphone_that_refuses_to_open_does_not_block_the_next_attempt()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, _) = Build(settings, models);
        using var _guard = service;
        capture.FailNextStart = true;

        string? error = null;
        service.Error += message => error = message;

        Assert.False(service.Start(new object(), _ => true));
        Assert.NotNull(error);
        Assert.Equal(DictationState.Idle, service.State);
        Assert.False(capture.IsRecording);

        // And the very next press works.
        string? delivered = null;
        Assert.True(service.Start(new object(), text => { delivered = text; return true; }));
        service.Stop();
        Assert.Equal("hello world", await WaitForDeliveryAsync(() => delivered));
    }

    /// <summary>One microphone, one destination: a second tile cannot take over mid-sentence.</summary>
    [Fact]
    public void A_second_start_while_recording_is_refused()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, _) = Build(settings, models);
        using var _guard = service;

        Assert.True(service.Start(new object(), _ => true));
        Assert.False(service.Start(new object(), _ => true));
    }

    [Fact]
    public async Task Cancelling_a_recording_delivers_nothing()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, engine) = Build(settings, models);
        using var _guard = service;

        var delivered = false;
        Assert.True(service.Start(new object(), _ => { delivered = true; return true; }));
        service.Cancel();

        Assert.Equal(DictationState.Idle, service.State);
        Assert.False(capture.IsRecording);        // detached at once, whatever the thread pool is doing
        Assert.Equal(0, engine.Calls);
        Assert.False(delivered);

        // Closing it is the slow half and runs on the thread pool; only that part has to be waited for.
        for (var i = 0; i < 100 && capture.StopCount == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, capture.StopCount);
    }

    /// <summary>
    /// Cancel, then press again immediately — the gesture of thinking better of a sentence and starting
    /// it over, and the one that used to break.
    /// </summary>
    /// <remarks>
    /// Closing an audio stream is up to two seconds of native work and runs on the thread pool. While it
    /// was the whole of "stop", the capture still believed it was recording for all of that time, so the
    /// next Start opened nothing at all: the service reported Recording, not one sample arrived, and the
    /// user was told their microphone had produced no audio.
    /// </remarks>
    [Fact]
    public void Starting_again_the_instant_a_recording_is_cancelled_opens_a_new_one()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, _) = Build(settings, models);
        using var _guard = service;

        Assert.True(service.Start(new object(), _ => true));
        service.Cancel();

        Assert.True(service.Start(new object(), _ => true));
        Assert.True(capture.IsRecording);
    }

    [Fact]
    public async Task Cancelling_during_transcription_delivers_nothing()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, engine) = Build(settings, models);
        using var _guard = service;
        engine.BlockUntilReleased = true;

        var delivered = false;
        Assert.True(service.Start(new object(), _ => { delivered = true; return true; }));
        service.Stop();
        Assert.Equal(DictationState.Transcribing, service.State);

        service.Cancel();
        engine.Release();

        await Task.Delay(100);
        Assert.False(delivered);
        Assert.Equal(DictationState.Idle, service.State);
    }

    /// <summary>
    /// An empty transcript is delivered nowhere and reported to nobody.
    /// </summary>
    /// <remarks>
    /// The engine ran and had nothing to say — a pause, a cough, a false start. A modal in exchange for
    /// that is a click demanded for no information, and the tile's own border already showed that the
    /// recording happened. A capture with no samples at all is the other case, and does complain.
    /// </remarks>
    [Fact]
    public async Task An_empty_transcript_is_neither_delivered_nor_announced()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, engine) = Build(settings, models);
        using var _guard = service;
        engine.Transcript = "   ";

        string? error = null;
        service.Error += message => error = message;

        var delivered = false;
        Assert.True(service.Start(new object(), _ => { delivered = true; return true; }));
        await Task.Delay(1_100);            // long enough to have been meant, which used to be the trigger
        service.Stop();

        await Task.Delay(200);
        Assert.False(delivered);
        Assert.Null(error);
        Assert.Equal(DictationState.Idle, service.State);
    }

    /// <summary>
    /// Whisper produces nonsense from a fragment barely longer than one of its own frames, so a tap on
    /// the key is padded rather than rejected.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]                 // nothing recorded stays nothing
    [InlineData(1, 20_000)]
    [InlineData(15_999, 20_000)]
    [InlineData(16_000, 16_000)]       // a full second is left alone
    [InlineData(40_000, 40_000)]
    public void Short_recordings_are_padded_to_a_length_the_model_can_use(int length, int expected)
        => Assert.Equal(expected, DictationService.Pad(new float[length]).Length);

    /// <summary>
    /// A microphone that yielded nothing at all: silent for a tap, reported for a held key.
    /// </summary>
    /// <remarks>
    /// By the clock, because there is no other way to tell the two apart — both arrive here as zero
    /// samples. A stray brush against the shortcut must not raise a dialog; a second of holding it and
    /// speaking has to say something, or a broken microphone is indistinguishable from a feature that
    /// silently does nothing.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1_100, true)]
    public async Task A_capture_that_yields_no_audio_is_reported_only_if_the_key_was_held(
        int heldMilliseconds, bool expectError)
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, _) = Build(settings, models);
        using var _guard = service;
        capture.Samples = [];

        string? error = null;
        service.Error += message => error = message;

        Assert.True(service.Start(new object(), _ => true));
        if (heldMilliseconds > 0)
            await Task.Delay(heldMilliseconds);
        service.Stop();

        await Task.Delay(100);
        Assert.Equal(expectError, error is not null);
    }

    /// <summary>
    /// A transcript with nowhere to go is reported, not dropped.
    /// <para>The tile it was meant for may have closed, or its shell may have exited, while the user was
    /// speaking. Saying nothing leaves them to work out from silence that a paragraph is gone.</para>
    /// </summary>
    [Fact]
    public async Task A_transcript_that_cannot_be_delivered_is_reported()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, _) = Build(settings, models);
        using var _guard = service;

        string? error = null;
        service.Error += message => error = message;

        Assert.True(service.Start(new object(), _ => false));    // nowhere to put it
        service.Stop();

        Assert.NotNull(await WaitForDeliveryAsync(() => error));
        Assert.Contains("hello world", error);                   // and it hands the words back
    }

    [Fact]
    public void Input_devices_come_from_the_capture_it_was_given()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, _, _) = Build(settings, models);
        using var _guard = service;

        Assert.Equal(["fake microphone"], service.GetInputDevices());
    }

    [Fact]
    public void Stop_without_a_recording_does_nothing()
    {
        using var settings = new TempSettings();
        using var models = new FakeModels();
        var (service, capture, engine) = Build(settings, models);
        using var _guard = service;

        service.Stop();
        service.Cancel();

        Assert.Equal(DictationState.Idle, service.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(0, engine.Calls);
    }
}
