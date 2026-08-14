namespace mTiles.Services.Speech;

/// <summary>
/// A recording taken out of a capture and not yet closed. Opaque on purpose — what is inside belongs to
/// the backend that made it — but a type rather than <c>object</c>, so a handle cannot be confused with
/// whatever else is being passed around a method that ends a dictation.
/// </summary>
internal interface IRecordingHandle;

/// <summary>There is no audio backend on this machine, or it could not be initialised.</summary>
/// <remarks>
/// Its own type because the two ways <see cref="IAudioCapture.Start"/> can refuse mean opposite things
/// to the person in front of the screen. This one is "your machine has no microphone we can open"; the
/// other is <see cref="AudioCaptureBusyException"/>, which is a bug in this application. Reported as one
/// message — "the microphone could not be opened" — they were indistinguishable, and the one that meant
/// "we got our own state wrong" was being blamed on the user's hardware.
/// </remarks>
internal sealed class AudioCaptureUnavailableException(string message) : Exception(message);

/// <summary>A recording is already attached to this capture.</summary>
/// <remarks>
/// Unreachable by design — <see cref="IAudioCapture.Detach"/> frees the capture before the slow half of
/// stopping runs — so reaching it means the state machine above has gone wrong, and it must be logged as
/// a fault rather than shown as a hardware problem.
/// </remarks>
internal sealed class AudioCaptureBusyException(string message) : Exception(message);

/// <summary>
/// A microphone, as the dictation pipeline needs it: start, stop, and one buffer of 16 kHz mono audio.
/// </summary>
/// <remarks>
/// Recording is bounded by the user holding a key, so there is no streaming contract here — the whole
/// utterance is transcribed once the key comes up. A level meter or a Silero VAD would need 30 ms frames
/// published as they arrive; nothing needs them today, and an interface that offered them anyway would
/// be describing a feature this app does not have.
/// </remarks>
internal interface IAudioCapture : IDisposable
{
    /// <summary>16 000. The rate the models are trained at and the only one they accept.</summary>
    const int SampleRate = 16_000;

    /// <summary>False when the audio backend could not be loaded at all — no microphone on this box,
    /// no portaudio, a container with no sound device. Dictation is then offered as unavailable rather
    /// than failing at the moment somebody presses the key.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether a recording is attached right now. Part of the contract rather than a
    /// convenience: <see cref="Detach"/> promises this goes false immediately, which is what lets the
    /// next recording start while the previous one is still being closed on another thread.</summary>
    bool IsRecording { get; }

    /// <summary>
    /// Names of the input devices, in the order the backend reports them.
    /// </summary>
    /// <param name="rescan">
    /// Ask the backend to look at the hardware again rather than answer from what it saw when it started.
    /// <para>Not a micro-optimisation the other way round: portaudio enumerates devices once, inside its
    /// initialisation, and every later call reads that snapshot — so a headset plugged in while the
    /// application was running could never appear, and a "Rescan" button without this answered with the
    /// same list for ever. Rescanning means tearing the library down and bringing it back, which closes
    /// any open stream, so it is asked for explicitly and refused while a recording is running.</para>
    /// </param>
    IReadOnlyList<string> GetInputDevices(bool rescan = false);

    /// <summary>Opens <paramref name="deviceName"/> (empty for the system default) and starts recording.</summary>
    void Start(string deviceName);

    /// <summary>
    /// Takes the live recording away from this capture and hands it back as an opaque token, leaving the
    /// microphone free to be started again at once. Null when nothing was recording.
    /// </summary>
    /// <remarks>
    /// Cheap and synchronous, and that is the whole point. Closing an audio stream is 50–150 ms of native
    /// work with a two-second drain behind it, so it belongs on the thread pool — but while it was there,
    /// the capture still believed it was recording, and a user who cancelled and immediately pressed
    /// again got a <see cref="Start"/> that quietly did nothing: no stream, not one sample, and (since
    /// an empty capture is now reported) a message telling them their microphone is broken. Detaching
    /// under the lock is what makes "stopped" true the moment the caller says so.
    /// </remarks>
    IRecordingHandle? Detach();

    /// <summary>Closes what <see cref="Detach"/> handed over and returns everything it captured, as
    /// 16 kHz mono samples in [-1,1]. The slow half; never call it on the UI thread.</summary>
    /// <remarks>
    /// There is deliberately no <c>Stop()</c> combining the two. Every caller here has something to do
    /// between them — the point of the split is that the microphone is released before the slow half
    /// runs — and a convenience method that puts them back together is one an accidental caller would
    /// use on the UI thread, which is the bug the split exists to prevent.
    /// </remarks>
    float[] Finish(IRecordingHandle? recording);
}
