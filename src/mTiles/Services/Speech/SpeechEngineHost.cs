using System.Diagnostics;

namespace mTiles.Services.Speech;

/// <summary>
/// Owns the loaded speech model: which engine holds it, how long it is kept, and the rule that nothing
/// unloads it while it is being used.
/// </summary>
/// <remarks>
/// <para>Lifted out of <c>DictationService</c>, which was doing two unrelated jobs — a state machine over
/// a microphone, and the custody of half a gigabyte of native memory. It moved <b>whole</b>, and that is
/// the condition on a split like this one: the semaphore, the engine, the path it was loaded from and the
/// idle timer are one invariant, and leaving any of them on the other side would have left the rule
/// spanning two classes instead of living in one.</para>
/// <para><b>The invariant.</b> Both engines are native. Unloading disposes a whisper.cpp context or a set
/// of ONNX sessions, and doing that while a transcription is running frees memory the native code is
/// reading — an access violation that takes the process with it, not an exception anybody can catch. So
/// loading, transcribing and unloading are serialised on <see cref="_inUse"/>, and every entry point
/// here takes it. Checking a state flag instead is not enough: a dictation can start between the check
/// and the call.</para>
/// <para>What is <em>not</em> here: whether a model should be unloaded at all. Zero minutes means never,
/// and that is a setting — the service reads it and either asks for a timer or does not.</para>
/// </remarks>
internal sealed class SpeechEngineHost : IDisposable
{
    private readonly Func<SpeechModelKind, ISpeechToTextEngine> _factory;
    private readonly Func<bool> _mayUnload;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _inUse = new(1, 1);

    private ISpeechToTextEngine? _engine;
    private SpeechModelKind? _kind;
    private string? _loadedPath;
    private Timer? _unloadTimer;
    private bool _disposed;

    /// <param name="factory">Builds an engine for a kind of model. The application passes nothing and
    /// gets <see cref="SpeechEngines.Create"/>; a test passes one that hands back its own.</param>
    /// <param name="mayUnload">Whether the idle timer is allowed to drop the model when it fires — the
    /// service's own "nothing is happening". Asked at the last moment, under the semaphore.</param>
    public SpeechEngineHost(Func<SpeechModelKind, ISpeechToTextEngine>? factory = null,
        Func<bool>? mayUnload = null)
    {
        _factory = factory ?? SpeechEngines.Create;
        _mayUnload = mayUnload ?? (() => true);
    }

    /// <summary>The model file or directory currently in memory, or null.</summary>
    public string? LoadedPath
    {
        get { lock (_gate) return _loadedPath; }
    }

    /// <summary>
    /// Loads <paramref name="path"/> if it is not already loaded and transcribes with it, as one
    /// indivisible use of the engine.
    /// </summary>
    /// <remarks>
    /// One method rather than a load and a transcribe, because the gap between them is precisely where
    /// the idle timer used to be able to unload the model that was about to be used.
    /// </remarks>
    public async Task<string> TranscribeAsync(SpeechModelKind kind, string path, float[] samples,
        TranscriptionOptions options, CancellationToken cancellationToken)
    {
        await _inUse.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var engine = Resolve(kind);
            await engine.LoadAsync(path, cancellationToken).ConfigureAwait(false);

            lock (_gate)
                _loadedPath = path;

            cancellationToken.ThrowIfCancellationRequested();
            return await engine.TranscribeAsync(samples, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inUse.Release();
        }
    }

    /// <summary>
    /// Drops the model from memory, so its files can be deleted.
    /// </summary>
    /// <param name="onlyIfPath">When given, only if that is what is loaded — deleting one model must not
    /// evict a different one that somebody is about to dictate with.</param>
    /// <remarks>
    /// Waits, unlike the idle timer: this is somebody clicking Delete, and a delete that quietly does
    /// nothing because a transcription was running is a button that lies. Ten seconds is longer than any
    /// transcription this application produces.
    /// </remarks>
    public void Unload(string? onlyIfPath = null)
    {
        if (!_inUse.Wait(TimeSpan.FromSeconds(10)))
        {
            Trace.TraceWarning("Could not unload the model: a transcription is still running.");
            return;
        }

        try
        {
            if (onlyIfPath is not null && !FileHelper.SamePath(LoadedPath, onlyIfPath))
                return;

            _engine?.Unload();
            lock (_gate)
                _loadedPath = null;
        }
        finally { _inUse.Release(); }
    }

    /// <summary>Drops the model after <paramref name="after"/> of nothing happening. Replaces any timer
    /// already set; <see cref="CancelScheduledUnload"/> takes it away entirely.</summary>
    public void ScheduleUnload(TimeSpan after)
    {
        lock (_gate)
        {
            // Under the lock, with disposal: transcriptions finish on thread-pool threads, so two of them
            // — or one of them and a shutdown — would otherwise swap this field at the same time, and the
            // loser's timer survives with nobody holding it.
            _unloadTimer?.Dispose();
            _unloadTimer = null;
            if (_disposed)
                return;

            _unloadTimer = new Timer(OnIdle, null, after, Timeout.InfiniteTimeSpan);
        }
    }

    public void CancelScheduledUnload()
    {
        lock (_gate)
        {
            _unloadTimer?.Dispose();
            _unloadTimer = null;
        }
    }

    private void OnIdle(object? _)
    {
        // On a thread-pool thread: an exception here would end the process, and no memory saving is worth
        // that.
        try
        {
            // Only if nothing is using the engine this instant. Not a wait: a transcription in flight
            // means the model is wanted, so the timer gives up rather than queueing behind it to unload
            // something that has just been used.
            if (!_inUse.Wait(0))
                return;

            try
            {
                if (!_mayUnload())
                    return;

                _engine?.Unload();

                // And it is no longer loaded. Leaving the path behind made the field describe a model
                // that is not in memory — harmless today, because it is only ever compared against, but a
                // field that lies is a trap for whoever reads it next.
                lock (_gate)
                    _loadedPath = null;
            }
            finally
            {
                _inUse.Release();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Unloading the speech model failed: {0}", ex);
        }
    }

    /// <summary>
    /// The engine for this kind of model, built on first use and replacing the one held when the user
    /// switches between a whisper model and a Parakeet one — two engines each holding half a gigabyte is
    /// not a state this application should be able to reach.
    /// </summary>
    private ISpeechToTextEngine Resolve(SpeechModelKind kind)
    {
        ISpeechToTextEngine? previous;
        ISpeechToTextEngine engine;

        lock (_gate)
        {
            if (_engine is not null && _kind == kind)
                return _engine;

            previous = _engine;
            engine = _factory(kind);
            _engine = engine;
            _kind = kind;
            _loadedPath = null;
        }

        previous?.Dispose();
        return engine;
    }

    /// <summary>
    /// Ends the timer and disposes the engine, if nothing is using it this instant.
    /// </summary>
    /// <remarks>
    /// Same hazard as the idle timer: disposing a native engine out from under a transcription crashes
    /// the process. But this runs on the UI thread while the window is closing, so it does not wait — a
    /// five-second pause on exit would be a worse bug than the one it prevents, and the process is about
    /// to end anyway, which frees the model far more thoroughly than Dispose would.
    /// <para>The semaphore itself is deliberately not disposed: a transcription that is still running
    /// holds it, and disposing it under that would surface as an <c>ObjectDisposedException</c> reported
    /// to the user as "Transcription failed" while the window closes.</para>
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _unloadTimer?.Dispose();
            _unloadTimer = null;
        }

        if (!_inUse.Wait(0))
        {
            Trace.TraceWarning("A transcription was still running at shutdown; the model goes with the process.");
            return;
        }

        try
        {
            _engine?.Dispose();
        }
        finally
        {
            _inUse.Release();
        }
    }
}
