using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace mTiles.Services.Speech;

/// <summary>
/// Microphone capture through portaudio, which is the one audio backend that ships prebuilt for both
/// RIDs this app targets — WASAPI on Windows and ALSA/Pulse on Linux behind the same call.
/// </summary>
/// <remarks>
/// The device is opened at its own sample rate and downsampled here, and the callback does nothing but
/// hand its buffer to a consumer thread. Both are Handy's arrangement (<c>audio_toolkit/audio/recorder.rs</c>)
/// and for the same reason: the callback runs on the driver's realtime thread, where filtering audio
/// means dropping it.
/// </remarks>
internal sealed class PortAudioCapture : IAudioCapture
{
    private readonly Lock _gate = new();

    private PortAudioSharp.Stream? _stream;

    private Recording? _recording;

    /// <summary>
    /// Everything one recording owns, so nothing is shared with the next one.
    /// </summary>
    /// <remarks>
    /// The consumer thread is only <em>asked</em> to finish — after two seconds <see cref="Finish"/> gives
    /// up waiting rather than lose the microphone. A consumer that outlives its recording therefore has
    /// to keep writing somewhere harmless: while this state lived in fields, it wrote into whatever
    /// recording had started since, appending the tail of one utterance to the beginning of the next.
    /// </remarks>
    private sealed record Recording(
        int Channels,
        AudioResampler Resampler,
        List<float> Samples,
        BlockingCollection<float[]> Chunks) : IRecordingHandle
    {
        public Task? Consumer { get; set; }

        /// <summary>The stream feeding it, so a detached recording carries everything needed to close it
        /// and nothing is left behind in a field for the next recording to trip over.</summary>
        public PortAudioSharp.Stream? Stream { get; set; }

        /// <summary>
        /// The delegate portaudio was given. Kept here, and not in a field, because portaudio holds only
        /// the raw function pointer: letting it be collected while the stream can still call it crashes
        /// the process from a thread we do not own. Tying it to the recording ties it to exactly the
        /// stream it belongs to — a field would have to be cleared at some moment, and every candidate
        /// moment is either too early or a race with the next recording.
        /// </summary>
        public PortAudioSharp.Stream.Callback? Callback { get; set; }
    }

    public bool IsAvailable => PortAudioRuntime.IsAvailable;

    public bool IsRecording
    {
        get { lock (_gate) return _stream is not null; }
    }

    public IReadOnlyList<string> GetInputDevices(bool rescan = false)
    {
        // Whether a rescan is safe is the runtime's own question, not this instance's: terminating
        // portaudio closes every open stream in the process, so what has to be true is that *nothing*
        // anywhere has one open. A rescan while recording is refused there and the list comes back as it
        // stands — the user is mid-sentence, and the one thing they must not lose to a button in Settings
        // is what they are saying.
        if (rescan)
            PortAudioRuntime.Reinitialize();

        // The whole loop inside the library's own lock, not merely the check that it is initialised.
        // These are indices into portaudio's device table, and a rescan on another thread — the settings
        // tab and the setup wizard each enumerate on their own — frees that table. Reading an entry out
        // of it while it is being freed is an access violation, which ends the process rather than
        // failing the call.
        return PortAudioRuntime.WithLibrary<IReadOnlyList<string>>(() =>
        {
            var names = new List<string>();
            try
            {
                for (var i = 0; i < PortAudio.DeviceCount; i++)
                {
                    var info = PortAudio.GetDeviceInfo(i);
                    if (info.maxInputChannels <= 0 || string.IsNullOrWhiteSpace(info.name))
                        continue;
                    if (!names.Contains(info.name))
                        names.Add(info.name);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[speech] enumerating input devices failed: {ex.Message}");
            }
            return names;
        }, []);
    }

    public void Start(string deviceName)
    {
        lock (_gate)
        {
            // Not a silent return. A caller asking to record while a recording is attached has made a
            // mistake, and swallowing it produced the worst possible outcome: the service believed it
            // was recording, no stream existed, and the user was eventually told their microphone had
            // produced no audio. Detach makes this genuinely unreachable; saying so out loud is what
            // keeps it that way.
            if (_stream is not null)
                throw new AudioCaptureBusyException(
                    "A recording is already attached to this capture; detach it before starting another.");

            // The whole of the native work inside the library's own lock, not merely the lookup that
            // starts it. This capture's lock is not enough on its own: it guards *this instance*, while
            // the thing a rescan tears down is process-wide, and relying on there being exactly one
            // capture is an assumption nothing states or enforces. Resolving the device under the lock
            // and then opening it outside left the gap that matters most — an index into a device table
            // that `Pa_Terminate` can free between the two.
            Recording? recording = null;

            var opened = PortAudioRuntime.WithLibrary<PortAudioSharp.Stream?>(
                () =>
                {
                    var device = ResolveDevice(deviceName);
                    var info = PortAudio.GetDeviceInfo(device);

                    var deviceRate = (int)Math.Round(info.defaultSampleRate > 0 ? info.defaultSampleRate : 44100);
                    var channels = Math.Max(1, Math.Min(info.maxInputChannels, 2));

                    var started = new Recording(
                        channels,
                        new AudioResampler(deviceRate, IAudioCapture.SampleRate),
                        [],
                        new BlockingCollection<float[]>(new ConcurrentQueue<float[]>()));
                    recording = started;

                    started.Callback = (IntPtr input, IntPtr _, uint frameCount,
                        ref StreamCallbackTimeInfo _, StreamCallbackFlags _, IntPtr _) =>
                    {
                        if (input != IntPtr.Zero && frameCount > 0)
                        {
                            var samples = new float[(int)frameCount * channels];
                            Marshal.Copy(input, samples, 0, samples.Length);

                            // Every exception, not just the InvalidOperationException of adding to a
                            // completed collection: if the consumer stopped early it has *disposed* the
                            // queue, and that is an ObjectDisposedException. This is the driver's
                            // realtime thread, called through a native function pointer — anything
                            // escaping here does not fail the recording, it ends the process.
                            try { started.Chunks.Add(samples); } catch { /* the recording is over */ }
                        }
                        return StreamCallbackResult.Continue;
                    };

                    var parameters = new StreamParameters
                    {
                        device = device,
                        channelCount = channels,
                        sampleFormat = SampleFormat.Float32,
                        suggestedLatency = info.defaultLowInputLatency,
                        hostApiSpecificStreamInfo = IntPtr.Zero,
                    };

                    // A start has to leave the capture either fully running or exactly as it was. A device
                    // that refuses to open — busy, unplugged between the enumeration and now, a format
                    // the driver will not take — throws out of Start(), and the half-built state used to
                    // stay behind: a live native stream nobody could reach, and a non-null _stream that
                    // made IsRecording true for ever. Detach could not clear it (there was no recording to
                    // detach), so every later attempt hit "a recording is already attached" and dictation
                    // was dead until the application restarted. One failed microphone, feature gone.
                    PortAudioSharp.Stream? stream = null;
                    try
                    {
                        stream = new PortAudioSharp.Stream(parameters, null, deviceRate,
                            PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, started.Callback, null);
                        stream.Start();
                    }
                    catch
                    {
                        // Nothing is draining the queue, so a consumer that somehow reaches it can finish;
                        // disposing the stream gives the device back.
                        try { started.Chunks.CompleteAdding(); } catch { /* nothing has it */ }
                        try { stream?.Dispose(); }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning("Closing a stream that failed to start: {0}", ex.Message);
                        }
                        recording = null;
                        throw;
                    }

                    // Registered before the lock is let go, so there is no instant at which a stream is
                    // running and the runtime believes nothing is.
                    PortAudioRuntime.StreamOpened();
                    return stream;
                },
                null);

            // Only the library being unavailable reaches this: anything that goes wrong once it is up
            // threw out of the block above, having already put back what it took.
            if (opened is null || recording is null)
                throw new AudioCaptureUnavailableException("Audio capture is unavailable on this machine.");

            recording.Consumer = Task.Factory.StartNew(() => Consume(recording), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            _stream = opened;
            _recording = recording;
        }
    }

    public IRecordingHandle? Detach()
    {
        lock (_gate)
        {
            var recording = _recording;
            if (recording is null)
                return null;

            recording.Stream = _stream;
            _stream = null;
            _recording = null;
            return recording;
        }
    }

    public float[] Finish(IRecordingHandle? detached)
    {
        if (detached is not Recording recording || recording.Stream is not { } stream)
            return [];

        // Both guarded, and separately. Closing the device is native work that can fail, and an
        // exception escaping here skipped everything below it: the queue was never completed, so the
        // consumer thread sat in GetConsumingEnumerable for the life of the process, and the caller
        // caught the throw and told the user their microphone had produced no audio — while the
        // recording sat finished in a buffer nobody went back for.
        try { stream.Stop(); }
        catch (Exception ex) { Trace.WriteLine($"[speech] stopping the capture stream failed: {ex.Message}"); }

        try { stream.Dispose(); }
        catch (Exception ex) { Trace.WriteLine($"[speech] closing the capture stream failed: {ex.Message}"); }

        // Only now is there one fewer native stream. Between Detach and here the stream is live and no
        // field names it, which is exactly the window a rescan must not tear the library down in.
        PortAudioRuntime.StreamClosed();

        // The consumer disposes the queue when it finishes, and it may have finished early — a fault in
        // the resampler ends it too. Completing a disposed collection throws, and this runs on the way
        // out of a recording and out of the application, where an exception costs more than the tail.
        try { recording.Chunks.CompleteAdding(); }
        catch (ObjectDisposedException) { }
        // The driver may still be delivering the buffer it was filling when we stopped; two seconds is
        // Handy's allowance for draining, and the alternative is losing the end of the sentence. If it
        // does run over, the samples land in this recording's own buffer, which nothing reads again.
        var drained = recording.Consumer?.Wait(TimeSpan.FromSeconds(2)) ?? true;
        if (!drained)
            Trace.TraceWarning("Audio consumer did not finish in time; the tail of the recording is lost.");

        lock (recording.Samples)
        {
            recording.Samples.AddRange(recording.Resampler.Flush());
            return [.. recording.Samples];
        }
    }

    public void Dispose() => Finish(Detach());

    /// <summary>
    /// Downmixes and resamples on a thread of its own, so the driver's callback only ever copies.
    /// <para>Everything it touches belongs to <paramref name="recording"/>. The lock is for the one
    /// hand-off that is genuinely concurrent: <see cref="Finish"/> reading the buffer while a consumer
    /// that outran its two seconds is still appending to it.</para>
    /// </summary>
    private static void Consume(Recording recording)
    {
        try
        {
            foreach (var chunk in recording.Chunks.GetConsumingEnumerable())
            {
                var mono = Downmix(chunk, recording.Channels);

                // The resampler is stateful, so it goes inside the lock with the buffer: a consumer that
                // outran its two seconds must not be inside Process while Stop is calling Flush.
                lock (recording.Samples)
                    recording.Samples.AddRange(recording.Resampler.Process(mono));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[speech] audio consumer stopped: {ex.Message}");
        }
        finally
        {
            // The last user of the queue closes it — Stop may have stopped waiting long before this.
            try { recording.Chunks.Dispose(); } catch { /* nothing left to tell */ }
        }
    }

    /// <summary>Interleaved frames to mono, by averaging — the same downmix Handy applies.</summary>
    internal static float[] Downmix(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
            return interleaved.ToArray();

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static int ResolveDevice(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            for (var i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0 && info.name == deviceName)
                    return i;
            }
            Trace.WriteLine($"[speech] input device '{deviceName}' is gone; falling back to the default.");
        }

        var @default = PortAudio.DefaultInputDevice;
        if (@default == PortAudio.NoDevice)
            throw new InvalidOperationException("No audio input device is available.");
        return @default;
    }
}

/// <summary>
/// One-time portaudio initialisation, kept apart so a machine without the native library disables
/// dictation instead of taking the application down when somebody opens Settings.
/// </summary>
internal static class PortAudioRuntime
{
    private static readonly Lock Gate = new();
    private static bool _tried;
    private static bool _available;

    /// <summary>
    /// How many native streams are open anywhere in this process.
    /// </summary>
    /// <remarks>
    /// <para>It lives here rather than on a capture because the resource it protects is here:
    /// <c>Pa_Terminate</c> closes every open stream in the process, including from under a callback on
    /// the driver's realtime thread, which is a crash rather than an exception. A count kept by one
    /// capture answers "is <em>this</em> object recording", and the code that asked it said so out loud —
    /// that relying on there being exactly one capture is an assumption nothing states or enforces. Now
    /// <see cref="Reinitialize"/> refuses on its own behalf, and no caller can forget to ask.</para>
    /// <para>Counted rather than flagged, because a stop and the next start legitimately overlap: a
    /// detached stream is still being closed on the thread pool for 50–150 ms after the field naming it
    /// has been cleared, and that window is precisely the one a rescan must not fire in.</para>
    /// </remarks>
    private static int _liveStreams;

    /// <summary>Called once a stream is running, before whoever opened it lets go of <see cref="Gate"/>,
    /// so there is no instant in which a live stream is invisible here.</summary>
    public static void StreamOpened()
    {
        lock (Gate)
            _liveStreams++;
    }

    /// <summary>Called once a stream has actually been closed — not when it was detached from the field
    /// that named it, which is up to two seconds earlier.</summary>
    public static void StreamClosed()
    {
        lock (Gate)
            _liveStreams = Math.Max(0, _liveStreams - 1);
    }

    /// <summary>Set once the library has been up at least once, and never cleared.</summary>
    /// <remarks>
    /// What makes <see cref="IsAvailable"/> answerable without the lock — see there for why that
    /// matters.
    /// </remarks>
    private static volatile bool _everUp;

    /// <summary>
    /// Whether the library can be used, answered without waiting for anything.
    /// </summary>
    /// <remarks>
    /// <b>Never takes the lock once the library has been up.</b> This is read on the dictation
    /// shortcut's path — which runs on the UI thread, for a keystroke, before every recording — and a
    /// rescan holds the lock for as long as <c>Pa_Terminate</c> plus <c>Pa_Initialize</c> take, which is
    /// the better part of a second. Waiting on it here would freeze typing in a terminal while somebody
    /// pressed Rescan in Settings. The answer during a rescan is the previous one, which is the right
    /// kind of stale: it says whether this machine has audio, not whether the library is mid-restart.
    /// </remarks>
    public static bool IsAvailable => _everUp ? _available : EnsureInitialized();

    public static bool EnsureInitialized()
    {
        lock (Gate)
            return EnsureInitializedLocked();
    }

    /// <summary>
    /// Loads and initialises the library, once.
    /// </summary>
    /// <remarks>
    /// A failure is remembered, and that is deliberate: the usual cause is that the native library is
    /// not there at all, and retrying a load that throws — on a path the shortcut reaches before every
    /// recording — costs an exception per attempt for a machine that is never going to grow a sound
    /// card. It is not a life sentence either: <see cref="Reinitialize"/> always tries again, and that
    /// is what the Rescan button in Settings does, so a transient failure has a way back that does not
    /// involve restarting the application.
    /// </remarks>
    /// <summary>
    /// How many times the library has actually been asked to enumerate the machine.
    /// </summary>
    /// <remarks>
    /// Nothing in the application reads it. It exists so that "a rescan was refused" is a fact a test can
    /// check without a microphone: whether <c>Pa_Terminate</c> ran is otherwise visible only to portaudio
    /// itself, and a guard nobody can observe is a guard nobody will notice the removal of.
    /// </remarks>
    internal static int Generation { get; private set; }

    private static bool EnsureInitializedLocked()
    {
        if (_tried)
            return _available;

        _tried = true;
        Generation++;
        try
        {
            PortAudio.LoadNativeLibrary();
            PortAudio.Initialize();
            _available = true;
            _everUp = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[speech] portaudio is unavailable: {ex.Message}");
            _available = false;
        }
        return _available;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the library initialised and held still.
    /// </summary>
    /// <remarks>
    /// Every call into portaudio that reads its state belongs in here. Enumerating devices is a loop of
    /// <c>Pa_GetDeviceCount</c> and <c>Pa_GetDeviceInfo</c> over indices into the library's own table,
    /// and <see cref="Reinitialize"/> throws that table away — from another thread, because the settings
    /// tab and the setup wizard each enumerate on their own. Reading an index out of a table being freed
    /// underneath is an access violation, which ends the process rather than failing the call.
    /// </remarks>
    public static T WithLibrary<T>(Func<T> body, T unavailable)
    {
        lock (Gate)
            return EnsureInitializedLocked() ? body() : unavailable;
    }

    /// <summary>
    /// Throws the library's device list away and builds a new one.
    /// </summary>
    /// <remarks>
    /// <para>portaudio enumerates the machine's devices once, inside <c>Pa_Initialize</c>, and holds that
    /// snapshot for as long as it is initialised. Everything after that — <c>Pa_GetDeviceCount</c>,
    /// <c>Pa_GetDeviceInfo</c> — reads the snapshot. So a headset plugged in after the first enumeration
    /// cannot appear, however many times it is asked for, and the "Rescan" button was answering with the
    /// same list every time. Terminating and initialising again is the API's own way of asking the
    /// question a second time.</para>
    /// <para><b>Refused while any stream is open.</b> <c>Pa_Terminate</c> closes every open stream,
    /// including from under a callback running on the driver's realtime thread, which is not an exception
    /// but a crash. The check is here, under the same lock as the teardown, so it cannot be raced and no
    /// caller can forget it: opening a stream and counting it are one step (<see cref="StreamOpened"/> is
    /// called before its opener releases this lock), so there is no moment in which a live stream is
    /// invisible to this.</para>
    /// <para>It always tries otherwise, even when the library is currently marked unavailable — this is
    /// the one path a user can ask for by hand, and refusing it would mean a single failed initialisation
    /// left dictation dead until the application was restarted.</para>
    /// </remarks>
    /// <returns>Whether the library is usable afterwards — which, when a stream was open, is simply
    /// whether it was usable before. Nothing was rescanned; the caller's list is the one it already had,
    /// and a user mid-sentence keeps their microphone.</returns>
    public static bool Reinitialize()
    {
        lock (Gate)
        {
            if (_liveStreams > 0)
            {
                Trace.WriteLine("[speech] not rescanning devices: a stream is still open.");
                return _available;
            }

            if (_tried && _available)
            {
                try
                {
                    PortAudio.Terminate();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[speech] terminating portaudio failed: {ex.Message}");
                }
            }

            // _available is left as it was until the attempt has an answer: IsAvailable reads it without
            // the lock, and a moment of "this machine has no audio" in the middle of a rescan would be
            // read by the shortcut as a reason to hand the key back to the terminal.
            _tried = false;
            return EnsureInitializedLocked();
        }
    }
}
