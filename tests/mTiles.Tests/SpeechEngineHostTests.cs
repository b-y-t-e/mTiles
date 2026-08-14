using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Custody of the loaded model: who holds it, when it is dropped, and the rule that nothing drops it
/// while it is being used.
/// </summary>
/// <remarks>
/// <para>The one class here whose failure mode is not an exception. Both engines are native — a
/// whisper.cpp context, a set of ONNX sessions — so unloading one while an inference is running frees
/// memory the native code is reading, and the process ends. Nothing catches that, no log line survives
/// it, and it happens on whichever machine happens to time it wrongly.</para>
/// <para>So the engine here counts what was done to it and blocks where a real one would be busy. What
/// is being pinned is ordering, not arithmetic: that a load and the transcription that needed it cannot
/// be separated, that the idle timer stands down rather than queueing, and that a delete aimed at one
/// model does not evict another.</para>
/// </remarks>
public class SpeechEngineHostTests
{
    private sealed class CountingEngine : ISpeechToTextEngine
    {
        private readonly Lock _gate = new();

        /// <summary>Released to let a transcription finish; null means it never blocks.</summary>
        public SemaphoreSlim? Hold { get; init; }

        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        public int Disposals { get; private set; }
        public string? LastPath { get; private set; }
        public bool IsLoaded { get; private set; }

        /// <summary>Set if a transcription was ever running while an unload ran — the crash, made
        /// observable.</summary>
        public bool Overlapped { get; private set; }
        private int _inFlight;

        public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (IsLoaded && LastPath == modelPath)
                    return Task.CompletedTask;

                Loads++;
                LastPath = modelPath;
                IsLoaded = true;
            }
            return Task.CompletedTask;
        }

        public void Unload()
        {
            lock (_gate)
            {
                if (_inFlight > 0)
                    Overlapped = true;
                if (IsLoaded)
                    Unloads++;
                IsLoaded = false;
            }
        }

        public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _inFlight++;

            try
            {
                if (Hold is { } hold)
                    await hold.WaitAsync(cancellationToken);
                return "words";
            }
            finally
            {
                lock (_gate)
                    _inFlight--;
            }
        }

        public void Dispose() => Disposals++;
    }

    private static SpeechEngineHost HostFor(ISpeechToTextEngine engine, Func<bool>? mayUnload = null) =>
        new(_ => engine, mayUnload);

    private static Task<string> Transcribe(SpeechEngineHost host, string path,
        SpeechModelKind kind = SpeechModelKind.WhisperGgml) =>
        host.TranscribeAsync(kind, path, [0.1f], new TranscriptionOptions(), CancellationToken.None);

    [Fact]
    public async Task The_model_is_loaded_once_and_kept_between_transcriptions()
    {
        var engine = new CountingEngine();
        using var host = HostFor(engine);

        Assert.Equal("words", await Transcribe(host, "model.bin"));
        Assert.Equal("words", await Transcribe(host, "model.bin"));

        Assert.Equal(1, engine.Loads);
        Assert.Equal("model.bin", host.LoadedPath);
    }

    /// <summary>Switching kind replaces the engine and disposes the one that was holding half a
    /// gigabyte — two resident at once is not a state this application should be able to reach.</summary>
    [Fact]
    public async Task Switching_to_another_kind_of_model_lets_go_of_the_previous_engine()
    {
        var whisper = new CountingEngine();
        var parakeet = new CountingEngine();
        using var host = new SpeechEngineHost(
            kind => kind == SpeechModelKind.ParakeetOnnx ? parakeet : whisper);

        await Transcribe(host, "ggml-base.bin");
        await Transcribe(host, "parakeet-dir", SpeechModelKind.ParakeetOnnx);

        Assert.Equal(1, whisper.Disposals);
        Assert.Equal(1, parakeet.Loads);
        Assert.Equal("parakeet-dir", host.LoadedPath);
    }

    /// <summary>
    /// Unloading names the model it means, so deleting one does not evict another.
    /// </summary>
    /// <remarks>
    /// Deleting a model the user has never dictated with used to drop whatever was resident, costing the
    /// next dictation a two-second reload of something else entirely.
    /// </remarks>
    [Fact]
    public async Task Unloading_a_different_model_leaves_the_loaded_one_alone()
    {
        var engine = new CountingEngine();
        using var host = HostFor(engine);
        await Transcribe(host, "in-use.bin");

        host.Unload("some-other.bin");

        Assert.Equal(0, engine.Unloads);
        Assert.Equal("in-use.bin", host.LoadedPath);

        host.Unload("in-use.bin");

        Assert.Equal(1, engine.Unloads);
        Assert.Null(host.LoadedPath);
    }

    /// <summary>
    /// The idle timer never unloads a model that is being used — it gives up instead of queueing.
    /// </summary>
    /// <remarks>
    /// This is the access violation, made observable: the fake engine records an unload that lands while
    /// a transcription is in flight. Queueing would be no better than racing — the model is wanted, and
    /// dropping it the instant the transcription that needed it finishes is exactly wrong.
    /// </remarks>
    [Fact]
    public async Task The_idle_timer_stands_down_while_a_transcription_is_running()
    {
        using var hold = new SemaphoreSlim(0, 1);
        var engine = new CountingEngine { Hold = hold };
        using var host = HostFor(engine);

        var running = Transcribe(host, "model.bin");
        host.ScheduleUnload(TimeSpan.FromMilliseconds(10));
        await Task.Delay(150);                       // the timer has fired by now, and found the engine busy

        Assert.Equal(0, engine.Unloads);

        hold.Release();
        Assert.Equal("words", await running);
        Assert.False(engine.Overlapped);
    }

    [Fact]
    public async Task An_idle_model_is_dropped_when_the_timer_fires()
    {
        var engine = new CountingEngine();
        using var host = HostFor(engine);
        await Transcribe(host, "model.bin");

        host.ScheduleUnload(TimeSpan.FromMilliseconds(20));

        for (var i = 0; i < 100 && engine.Unloads == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, engine.Unloads);
        Assert.Null(host.LoadedPath);
    }

    /// <summary>The host does not decide whether the model may go — the service does, and it is asked at
    /// the last moment.</summary>
    [Fact]
    public async Task A_model_is_not_dropped_while_the_service_says_it_is_busy()
    {
        var engine = new CountingEngine();
        using var host = HostFor(engine, mayUnload: () => false);
        await Transcribe(host, "model.bin");

        host.ScheduleUnload(TimeSpan.FromMilliseconds(10));
        await Task.Delay(150);

        Assert.Equal(0, engine.Unloads);
        Assert.Equal("model.bin", host.LoadedPath);
    }

    [Fact]
    public async Task A_cancelled_timer_does_not_fire()
    {
        var engine = new CountingEngine();
        using var host = HostFor(engine);
        await Transcribe(host, "model.bin");

        host.ScheduleUnload(TimeSpan.FromMilliseconds(20));
        host.CancelScheduledUnload();
        await Task.Delay(150);

        Assert.Equal(0, engine.Unloads);
    }

    /// <summary>
    /// Shutdown does not wait for a transcription, and does not dispose the engine underneath one.
    /// </summary>
    /// <remarks>
    /// This runs on the UI thread while the window is closing. Waiting would be a five-second freeze on
    /// exit; disposing anyway would be the crash. Leaving the model to the process is neither — it is
    /// about to end, which frees it more thoroughly than Dispose would.
    /// </remarks>
    [Fact]
    public async Task Disposing_while_a_transcription_runs_leaves_the_engine_to_the_process()
    {
        using var hold = new SemaphoreSlim(0, 1);
        var engine = new CountingEngine { Hold = hold };
        var host = HostFor(engine);

        var running = Transcribe(host, "model.bin");
        host.Dispose();

        Assert.Equal(0, engine.Disposals);
        Assert.False(engine.Overlapped);

        hold.Release();
        await running;
    }

    [Fact]
    public async Task Disposing_an_idle_host_disposes_its_engine()
    {
        var engine = new CountingEngine();
        var host = HostFor(engine);
        await Transcribe(host, "model.bin");

        host.Dispose();

        Assert.Equal(1, engine.Disposals);
    }

    /// <summary>A timer set after disposal is not a timer that fires on a disposed engine.</summary>
    [Fact]
    public async Task Scheduling_an_unload_after_disposal_does_nothing()
    {
        var engine = new CountingEngine();
        var host = HostFor(engine);
        await Transcribe(host, "model.bin");

        host.Dispose();
        host.ScheduleUnload(TimeSpan.FromMilliseconds(10));
        await Task.Delay(120);

        Assert.Equal(1, engine.Disposals);
        Assert.Equal(0, engine.Unloads);
    }
}
