using System.Buffers.Binary;
using mTiles.Services.Speech;

namespace mTiles.Services.Phone;

/// <summary>
/// A microphone that happens to be in someone's hand rather than plugged into this machine.
/// </summary>
/// <remarks>
/// Implements <see cref="IAudioCapture"/> and nothing else, which is the whole reason the dictation
/// pipeline needed no changes to gain a second input: <see cref="DictationService"/> asks for 16 kHz mono
/// samples and does not care which continent they were spoken on. Recognition, post-processing and
/// delivery into the tile are the code that was already there.
/// <para><b>Why the browser sends raw PCM.</b> <c>MediaRecorder</c> produces webm/opus on Android and
/// mp4/aac on iOS, so accepting its output would mean shipping a decoder for two container formats to
/// solve a problem that does not need solving. An <c>AudioWorklet</c> hands the page float samples
/// directly; converting those to 16-bit and sending them costs 32 KB/s on a network that is either a LAN
/// or a WireGuard tunnel. No codec, no dependency, and the samples arrive in the shape the pipeline
/// already wants.</para>
/// <para><b>Why the resampling is here.</b> An <c>AudioContext</c> cannot be relied on to honour a
/// requested rate — iOS ignores it and runs at the hardware's — so the page reports whatever rate it got
/// and this converts, using the same <see cref="AudioResampler"/> that the local microphone path has been
/// using all along. Doing it in JavaScript would have meant a second, untested resampler.</para>
/// </remarks>
internal sealed class PhoneAudioCapture : IAudioCapture
{
    /// <summary>The name this appears under in the device list.</summary>
    public const string DeviceName = "Phone";

    /// <summary>
    /// Five minutes at 16 kHz. A stream that runs past this stops being appended to.
    /// </summary>
    /// <remarks>
    /// <see cref="DictationService"/> already caps a recording by time, but that timer is only armed when
    /// the recording starts through it. This bound belongs to the socket instead: the samples arrive from
    /// another device over a network, so "the phone stopped sending" is a thing that can simply never
    /// happen — a pocketed phone with a wedged page holds the connection open — and unbounded growth on
    /// the far end of a network is a memory exhaustion bug waiting for a bad Wi-Fi day.
    /// </remarks>
    internal const int MaxSamples = IAudioCapture.SampleRate * 60 * 5;

    private readonly Lock _gate = new();

    /// <summary>Prepared by the socket, waiting to be claimed by <see cref="Start"/>.</summary>
    private Recording? _incoming;
    private Recording? _active;
    private bool _disposed;

    /// <summary>Always true: this capture needs no audio backend on this machine at all.</summary>
    public bool IsAvailable => true;

    public bool IsRecording
    {
        get { lock (_gate) return _active is not null; }
    }

    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => [DeviceName];

    /// <summary>
    /// Opens a buffer for a stream the phone is about to send, at the rate the phone reports.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Start"/>, and called first, because of an ordering that cannot be avoided:
    /// the socket learns the sample rate on a background thread, while starting a dictation has to happen
    /// on the UI thread. Between those two points audio is already arriving. Buffering into a recording
    /// that <see cref="Start"/> later adopts is what stops the first syllable of every utterance being
    /// dropped on the floor — which, in a push-to-talk feature, is the first word.
    /// </remarks>
    public void PrepareForStream(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var recording = new Recording(new AudioResampler(sampleRate, IAudioCapture.SampleRate));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A live recording is never replaced. The server only ever sends one `begin` at a time, so
            // reaching here while something is recording means that rule has been broken somewhere — and
            // silently swapping the buffer would strand the recording the tile is still showing.
            if (_active is not null)
                throw new AudioCaptureBusyException("A phone recording is already in progress.");

            _incoming = recording;
        }
    }

    public void Start(string deviceName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
                throw new AudioCaptureBusyException("A phone recording is already attached to this capture.");

            _active = _incoming
                      ?? throw new AudioCaptureUnavailableException("No phone is streaming audio.");
            _incoming = null;
        }
    }

    /// <summary>
    /// Appends one frame of 16-bit little-endian mono PCM as it comes off the socket.
    /// </summary>
    /// <remarks>
    /// Accepts frames before <see cref="Start"/> has claimed the recording, for the reason above. Frames
    /// arriving when there is no recording at all — after the user let go, or before the phone announced
    /// itself — are dropped rather than throwing: the sender is a network peer, and a peer that sends at
    /// the wrong moment is a normal event, not an exception.
    /// </remarks>
    public void Write(ReadOnlySpan<byte> pcm16LittleEndian)
    {
        // The 16-bit conversion happens outside the lock; the resampling cannot. It is stateful — the
        // kernel needs the tail of the previous frame as the left context of this one — so moving it out
        // would mean two frames interleaving their way through one filter and producing something that is
        // not the audio anybody spoke. The lock is therefore held for the length of one frame's
        // convolution, about a millisecond, and Detach may wait that long. (An earlier comment here
        // claimed the conversion was the only costly part and that the resampling was outside the lock;
        // neither was true.)
        var sampleCount = pcm16LittleEndian.Length / sizeof(short);
        if (sampleCount == 0)
            return;

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm16LittleEndian[(i * 2)..]);
            // short.MinValue has no positive counterpart, so dividing by 32768 keeps the result inside
            // [-1,1] where the models expect it, at the cost of never quite reaching +1.
            samples[i] = value / 32768f;
        }

        lock (_gate)
        {
            var recording = _active ?? _incoming;
            if (recording is null || _disposed)
                return;

            if (recording.Samples.Count >= MaxSamples)
                return;

            recording.Samples.AddRange(recording.Resampler.Process(samples));
        }
    }

    public IRecordingHandle? Detach()
    {
        lock (_gate)
        {
            var recording = _active;
            _active = null;
            return recording;
        }
    }

    public float[] Finish(IRecordingHandle? recording)
    {
        if (recording is not Recording taken)
            return [];

        lock (_gate)
        {
            // Flushed once. A second call would append another tail to samples that already had one, so
            // the utterance would end with a few milliseconds of itself repeated — inaudible in a
            // waveform, and exactly the kind of thing that turns into a wrong word.
            if (!taken.Flushed)
            {
                taken.Flushed = true;

                // The tail the kernel could not produce without samples to its right: the end of the
                // last word.
                taken.Samples.AddRange(taken.Resampler.Flush());
            }

            return [.. taken.Samples];
        }
    }

    /// <summary>Drops any buffered audio for a stream that ended without a recording being started.</summary>
    public void AbandonIncoming()
    {
        lock (_gate)
            _incoming = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _incoming = null;
            _active = null;
        }
    }

    private sealed record Recording(AudioResampler Resampler) : IRecordingHandle
    {
        public List<float> Samples { get; } = [];

        /// <summary>Whether the resampler's tail has already been appended. See <see cref="Finish"/>.</summary>
        public bool Flushed { get; set; }
    }
}
