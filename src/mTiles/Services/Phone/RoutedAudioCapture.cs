using mTiles.Services.Speech;

namespace mTiles.Services.Phone;

/// <summary>
/// One <see cref="IAudioCapture"/> in front of two, so a dictation can be taken from the local microphone
/// or from a paired phone without <see cref="DictationService"/> knowing there is a choice.
/// </summary>
/// <remarks>
/// The alternative was a second audio path through the dictation service, chosen by a flag — which would
/// have put "where did this audio come from" into every method that touches a recording, for a difference
/// that ends the moment the samples exist. A decorator keeps it at the one place the two actually differ.
/// <para><b>Handles carry their own owner.</b> <see cref="IAudioCapture.Detach"/> and
/// <see cref="IAudioCapture.Finish"/> are deliberately split so the slow half can run off the UI thread,
/// which means a *new* recording can legally start on a different backend while the previous one is still
/// being closed. Routing <c>Finish</c> by "whichever backend is current" would then finish the wrong
/// recording on the wrong device — silence delivered to the tile, and the real audio dropped. Tagging the
/// handle at <c>Detach</c> makes that unrepresentable rather than merely unlikely.</para>
/// </remarks>
internal sealed class RoutedAudioCapture : IAudioCapture
{
    private readonly IAudioCapture local;
    private readonly PhoneAudioCapture phone;
    private readonly Lock _gate = new();

    public RoutedAudioCapture(IAudioCapture local, PhoneAudioCapture phone)
    {
        this.local = local;
        this.phone = phone;
        _current = local;
    }

    /// <summary>Set just before a phone-driven dictation starts; consumed by the next <see cref="Start"/>.</summary>
    private bool _nextFromPhone;
    private IAudioCapture _current;

    public PhoneAudioCapture Phone => phone;

    /// <summary>True while the recording in progress is coming from a phone.</summary>
    public bool IsRecordingFromPhone
    {
        get { lock (_gate) return ReferenceEquals(_current, phone) && phone.IsRecording; }
    }

    /// <summary>
    /// Sends the next <see cref="Start"/> to the phone instead of the local microphone.
    /// </summary>
    /// <remarks>
    /// A one-shot rather than a mode, and cleared by the <c>Start</c> that consumes it, so a phone that
    /// disconnects mid-gesture cannot leave the local microphone button permanently pointing at a device
    /// that is no longer there.
    /// </remarks>
    public void RouteNextToPhone() { lock (_gate) _nextFromPhone = true; }

    /// <summary>Cancels a pending route, for a phone stream that ended before dictation could start.</summary>
    public void CancelPhoneRoute()
    {
        lock (_gate) _nextFromPhone = false;
        phone.AbandonIncoming();
    }

    /// <summary>
    /// Whether the capture that is about to be used can be opened.
    /// </summary>
    /// <remarks>
    /// Not simply the local microphone's answer, and the difference is the whole point of the feature.
    /// <see cref="DictationService.Start"/> refuses outright when this is false, so on a machine with no
    /// working audio backend — no sound device, no portaudio, a server nobody has ever plugged a
    /// microphone into — a phone could not dictate either, despite needing nothing from that machine's
    /// hardware. Those are exactly the machines this exists for: the far end of a remote desktop session.
    /// <para>While a phone stream is armed the answer is yes; the rest of the time it is the microphone's,
    /// so Settings still reports honestly that this machine has no audio input.</para>
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_nextFromPhone || ReferenceEquals(_current, phone) && phone.IsRecording)
                    return true;
            }

            return local.IsAvailable;
        }
    }

    public bool IsRecording => local.IsRecording || phone.IsRecording;

    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => local.GetInputDevices(rescan);

    public void Start(string deviceName)
    {
        IAudioCapture target;
        IAudioCapture previous;

        lock (_gate)
        {
            previous = _current;
            target = _nextFromPhone ? phone : local;
            _nextFromPhone = false;
            _current = target;
        }

        try
        {
            target.Start(deviceName);
        }
        catch
        {
            // Put it back. A capture that refused to start is not the current one, and leaving the field
            // pointing at it means the next Detach asks the wrong backend — which answers null, so the
            // recording that did start can never be finished.
            lock (_gate)
                _current = previous;

            throw;
        }
    }

    public IRecordingHandle? Detach()
    {
        IAudioCapture target;
        lock (_gate)
            target = _current;

        return target.Detach() is { } handle ? new RoutedHandle(target, handle) : null;
    }

    public float[] Finish(IRecordingHandle? recording) =>
        recording is RoutedHandle routed ? routed.Owner.Finish(routed.Inner) : [];

    public void Dispose()
    {
        local.Dispose();
        phone.Dispose();
    }

    private sealed record RoutedHandle(IAudioCapture Owner, IRecordingHandle Inner) : IRecordingHandle;
}
