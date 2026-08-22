using System.Buffers.Binary;
using mTiles.Services.Phone;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The phone as a microphone, and the switch that puts it in front of the local one.
/// </summary>
/// <remarks>
/// Both implement <see cref="IAudioCapture"/>, which is the whole reason the dictation service needed no
/// changes to gain a second input — so the contract they have to keep is the one the service relies on,
/// and these are the parts of it that are easy to get wrong.
/// </remarks>
public class PhoneAudioTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    [Fact]
    public void Audio_at_the_models_own_rate_passes_through_unchanged()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);
        capture.Start(PhoneAudioCapture.DeviceName);
        capture.Write(Pcm(0, 16384, -16384, 32767));

        var samples = capture.Finish(capture.Detach());

        Assert.Equal(4, samples.Length);
        Assert.Equal(0.5f, samples[1], 3);
        Assert.Equal(-0.5f, samples[2], 3);
    }

    [Fact]
    public void Audio_at_the_phones_own_rate_is_resampled_to_sixteen_kilohertz()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(48_000);
        capture.Start(PhoneAudioCapture.DeviceName);

        // One second of 48 kHz silence-shaped input; only the count matters here.
        capture.Write(Pcm(new short[48_000]));
        var samples = capture.Finish(capture.Detach());

        // A windowed-sinc resampler cannot produce the very last few samples without input to their
        // right, so the count lands just under a second rather than exactly on it.
        Assert.InRange(samples.Length, 15_500, 16_100);
    }

    /// <summary>
    /// The phone announces itself on a background thread and dictation starts on the UI thread, so audio
    /// is already arriving in between. Dropping it loses the first word of every utterance.
    /// </summary>
    [Fact]
    public void Audio_arriving_before_the_recording_starts_is_kept()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);

        capture.Write(Pcm(16384, 16384));       // before Start
        capture.Start(PhoneAudioCapture.DeviceName);
        capture.Write(Pcm(16384));              // after

        Assert.Equal(3, capture.Finish(capture.Detach()).Length);
    }

    [Fact]
    public void Audio_arriving_with_no_stream_at_all_is_dropped_rather_than_thrown()
    {
        var capture = new PhoneAudioCapture();

        capture.Write(Pcm(1, 2, 3));   // a peer sending at the wrong moment is normal, not exceptional

        Assert.False(capture.IsRecording);
        Assert.Empty(capture.Finish(capture.Detach()));
    }

    [Fact]
    public void Starting_with_no_stream_announced_is_refused_as_unavailable()
    {
        var capture = new PhoneAudioCapture();

        Assert.Throws<AudioCaptureUnavailableException>(() => capture.Start(PhoneAudioCapture.DeviceName));
    }

    /// <summary>
    /// The samples come from another device over a network, so "the phone stopped sending" is a thing
    /// that can simply never happen. Unbounded growth on the far end of a network is a bad Wi-Fi day
    /// away from exhausting memory.
    /// </summary>
    [Fact]
    public void A_stream_that_never_ends_stops_being_buffered()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);
        capture.Start(PhoneAudioCapture.DeviceName);

        var minute = Pcm(new short[16_000 * 60]);
        for (var i = 0; i < 8; i++)
            capture.Write(minute);

        var samples = capture.Finish(capture.Detach()).Length;

        // Both ends. The lower bound used to be zero, so this passed just as happily on a recording that
        // had captured nothing at all — which is the opposite of what it claims to check.
        Assert.InRange(samples, PhoneAudioCapture.MaxSamples - 16_000, PhoneAudioCapture.MaxSamples + 16_000);
    }

    /// <summary>
    /// Once the recording has been taken away, later frames belong to nothing.
    /// </summary>
    /// <remarks>
    /// The socket does not stop the instant the user lets go — the last frames are already in flight, and
    /// Detach happens on the UI thread while they arrive. Appending them to the detached recording would
    /// put audio into a buffer that has already been handed to the transcriber.
    /// </remarks>
    [Fact]
    public void Audio_arriving_after_the_recording_was_taken_away_is_dropped()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);
        capture.Start(PhoneAudioCapture.DeviceName);
        capture.Write(Pcm(16384, 16384));

        var handle = capture.Detach();
        capture.Write(Pcm(16384, 16384, 16384));   // too late

        Assert.Equal(2, capture.Finish(handle).Length);
    }

    /// <summary>
    /// Finishing twice must not append the resampler's tail twice.
    /// </summary>
    /// <remarks>
    /// A few milliseconds of the utterance repeated at its end is inaudible in a waveform and exactly the
    /// kind of thing that turns into a wrong word.
    /// </remarks>
    [Fact]
    public void Finishing_twice_returns_the_same_audio()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(48_000);
        capture.Start(PhoneAudioCapture.DeviceName);
        capture.Write(Pcm(new short[4800]));

        var handle = capture.Detach();
        var first = capture.Finish(handle);
        var second = capture.Finish(handle);

        Assert.Equal(first, second);
    }

    /// <summary>A live recording is never swapped out from under the tile that is showing it.</summary>
    [Fact]
    public void Preparing_a_second_stream_while_one_is_recording_is_refused()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);
        capture.Start(PhoneAudioCapture.DeviceName);

        Assert.Throws<AudioCaptureBusyException>(() => capture.PrepareForStream(16_000));
    }

    [Fact]
    public void Detaching_frees_the_capture_at_once()
    {
        var capture = new PhoneAudioCapture();
        capture.PrepareForStream(16_000);
        capture.Start(PhoneAudioCapture.DeviceName);
        Assert.True(capture.IsRecording);

        var handle = capture.Detach();

        Assert.False(capture.IsRecording);
        Assert.NotNull(handle);
    }

    // ── the router ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// While a phone stream is armed the router reports itself available, whatever the local hardware says.
    /// </summary>
    /// <remarks>
    /// <see cref="DictationService.Start"/> refuses outright when this is false, so on a machine with no
    /// working audio backend — the far end of a remote desktop session, which is what this feature is for
    /// — a phone could not dictate either.
    /// </remarks>
    [Fact]
    public void An_armed_route_makes_the_router_available_without_local_audio()
    {
        var router = new RoutedAudioCapture(new SilentCapture(available: false), new PhoneAudioCapture());

        Assert.False(router.IsAvailable);

        router.Phone.PrepareForStream(16_000);
        router.RouteNextToPhone();

        Assert.True(router.IsAvailable);
    }

    /// <summary>Cancelling puts it back, so the microphone button is not left pointing at a phone.</summary>
    [Fact]
    public void Cancelling_the_route_gives_the_microphone_back()
    {
        var local = new FakeCapture();
        var router = new RoutedAudioCapture(local, new PhoneAudioCapture());

        router.Phone.PrepareForStream(16_000);
        router.RouteNextToPhone();
        router.CancelPhoneRoute();

        router.Start("default");

        Assert.True(local.Started);
        Assert.False(router.IsRecordingFromPhone);
    }

    /// <summary>A capture that is present but records nothing, or reports itself absent.</summary>
    private sealed class SilentCapture(bool available = true) : IAudioCapture
    {
        public bool IsAvailable => available;
        public bool IsRecording => false;

        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["silent"];

        public void Start(string deviceName) { }

        public IRecordingHandle? Detach() => null;

        public float[] Finish(IRecordingHandle? recording) => [];

        public void Dispose() { }
    }

    [Fact]
    public void Without_a_route_the_local_microphone_is_used()
    {
        var local = new FakeCapture();
        var router = new RoutedAudioCapture(local, new PhoneAudioCapture());

        router.Start("default");

        Assert.True(local.Started);
        Assert.False(router.IsRecordingFromPhone);
    }

    [Fact]
    public void A_route_sends_one_recording_to_the_phone_and_then_lapses()
    {
        var local = new FakeCapture();
        var phone = new PhoneAudioCapture();
        var router = new RoutedAudioCapture(local, phone);

        phone.PrepareForStream(16_000);
        router.RouteNextToPhone();
        router.Start("default");

        Assert.True(router.IsRecordingFromPhone);
        Assert.False(local.Started);

        router.Finish(router.Detach());

        // The route was consumed, so a phone that disconnects cannot leave the microphone button
        // pointing at a device that is no longer there.
        router.Start("default");
        Assert.True(local.Started);
    }

    /// <summary>
    /// Detach and Finish are split so the slow half runs off the UI thread, which means a new recording
    /// can legally start on the other backend while the previous one is still being closed. Routing
    /// Finish by "whichever is current" would finish the wrong recording on the wrong device.
    /// </summary>
    [Fact]
    public void A_recording_is_finished_by_the_backend_that_started_it()
    {
        var local = new FakeCapture([0.25f, 0.5f]);
        var phone = new PhoneAudioCapture();
        var router = new RoutedAudioCapture(local, phone);

        router.Start("default");
        var localHandle = router.Detach();

        // A phone recording begins before the local one has been closed.
        phone.PrepareForStream(16_000);
        router.RouteNextToPhone();
        router.Start("default");

        var samples = router.Finish(localHandle);

        Assert.Equal([0.25f, 0.5f], samples);
    }

    private sealed class FakeCapture(float[]? samples = null) : IAudioCapture
    {
        private readonly float[] _samples = samples ?? [];

        public bool Started { get; private set; }
        public bool IsAvailable => true;
        public bool IsRecording { get; private set; }

        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["default"];

        public void Start(string deviceName)
        {
            Started = true;
            IsRecording = true;
        }

        public IRecordingHandle? Detach()
        {
            if (!IsRecording) return null;
            IsRecording = false;
            return new Handle(_samples);
        }

        public float[] Finish(IRecordingHandle? recording) =>
            recording is Handle handle ? handle.Samples : [];

        public void Dispose() { }

        private sealed record Handle(float[] Samples) : IRecordingHandle;
    }
}
