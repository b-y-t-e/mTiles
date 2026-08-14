using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Speech;

public enum DictationState
{
    Idle,
    Recording,
    Transcribing
}

/// <summary>
/// The dictation feature, as one object: record while asked to, transcribe when told to stop, hand the
/// text to whoever started it.
/// </summary>
/// <remarks>
/// <para>One recording at a time, for the whole application. Two microphones open on one machine is not
/// a thing worth supporting, and "which tile is this going to?" has to have one answer.</para>
/// <para>The delivery callback is supplied by the caller at <see cref="Start"/> rather than resolved
/// here, so this class never has to know what a tile is — the same reason it takes a dispatcher instead
/// of reaching for Avalonia's.</para>
/// </remarks>
public sealed class DictationService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IAudioCapture _capture;
    private readonly SpeechModelStore _store;
    private readonly Action<Action> _dispatch;
    private readonly Lock _gate = new();

    /// <summary>
    /// The loaded model and everything that governs it — which engine holds it, how long it is kept, and
    /// the rule that nothing unloads it while it is in use.
    /// </summary>
    /// <remarks>
    /// A class of its own because it is a whole invariant rather than a few fields: see
    /// <see cref="SpeechEngineHost"/>. What stays here is the decision it is not entitled to make —
    /// whether the model is ever dropped at all, which is a setting.
    /// </remarks>
    private readonly SpeechEngineHost _engines;

    private Func<string, bool>? _deliver;

    /// <summary>When the current recording began. What separates a tap on the shortcut from a sentence
    /// that came back with nothing — the two need opposite answers, and the sample count cannot tell
    /// them apart when the microphone yields nothing at all.</summary>
    private long _recordingStarted;

    private CancellationTokenSource? _work;
    private Timer? _recordingLimit;
    private bool _disposed;

    /// <summary>Internal because the seams it offers — a fake microphone, a fake engine — are this
    /// assembly's and its tests', not an API.</summary>
    /// <param name="engine">When given, used for every kind of model. That is what a test wants; the
    /// application passes nothing and gets the engine each model actually needs.</param>
    /// <param name="maxRecording">Overridable so the cap can be tested in milliseconds rather than in
    /// the five minutes a real recording is given.</param>
    /// <param name="unloadAfter">Likewise for the idle model unload, whose setting is in whole minutes.
    /// Both are timers guarding something expensive — an hour of accidental audio, half a gigabyte of
    /// resident memory — and neither was testable at its real scale.</param>
    internal DictationService(SettingsService settings,
        IAudioCapture? capture = null,
        ISpeechToTextEngine? engine = null,
        SpeechModelStore? store = null,
        Action<Action>? dispatch = null,
        TimeSpan? maxRecording = null,
        TimeSpan? unloadAfter = null)
    {
        _maxRecording = maxRecording ?? DefaultMaxRecording;
        _unloadAfter = unloadAfter;
        _settings = settings;
        _capture = capture ?? new PortAudioCapture();
        _engines = new SpeechEngineHost(
            engine is not null ? _ => engine : null,
            // The host does not know what this service is doing, and does not need to: it asks, at the
            // last moment and under its own lock, whether dropping the model now would be taking it away
            // from something.
            mayUnload: () => State == DictationState.Idle);
        _store = store ?? new SpeechModelStore();
        _dispatch = dispatch ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        // Turning dictation on, choosing a model or downloading one all change whether a tile may
        // record. There is nothing else for a tile to watch, so the same signal carries both.
        _settings.SettingsChanged += RaiseStateChanged;
    }

    /// <summary>The speech settings as they stand now. Read, never cached — they change under us.</summary>
    public SpeechSettings Speech => _settings.Settings.Speech;

    /// <summary>
    /// What the service is doing, for anything that draws it or checks before asking.
    /// </summary>
    /// <remarks>
    /// <para>Written only inside <see cref="_gate"/> — every <c>SetState</c> call is under that lock —
    /// and read without it. Deliberately, and not because an <c>enum</c> write happens to be atomic:
    /// <b>no decision is made on this property.</b> <see cref="Start"/>, <see cref="Stop"/> and
    /// <see cref="Cancel"/> each re-read it under the lock and act there, so the worst a stale read can
    /// do is send a click into a <c>Start</c> that then refuses it with a reason — which is what a click
    /// during transcription gets anyway.</para>
    /// <para>Nothing reads it out of nowhere either: every change is followed by
    /// <see cref="StateChanged"/> raised on the dispatcher, and the post itself orders the write ahead of
    /// the handler that reacts to it. <c>volatile</c> here would buy a fence for reads that are already
    /// either ordered or provisional, and would suggest the property is safe to branch on.</para>
    /// </remarks>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Whatever the caller passed as <c>owner</c> to <see cref="Start"/> — the tile, in practice.
    /// Null when idle.</summary>
    public object? Owner { get; private set; }

    /// <summary>Raised on the dispatcher whenever <see cref="State"/> or <see cref="Owner"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Something the user needs to know: no model, no microphone, a failed transcription.
    /// Raised on the dispatcher, like <see cref="StateChanged"/> — a handler may touch the UI
    /// directly.</summary>
    public event Action<string>? Error;

    public SpeechModelStore Store => _store;

    /// <summary>False when there is no audio backend at all; the microphone button says so rather than
    /// waiting to fail under the user's finger.</summary>
    public bool IsAudioAvailable => _capture.IsAvailable;

    /// <summary>
    /// The model dictation would use: the one chosen in settings, or the catalogue's default when the
    /// setting is empty or names something that is no longer offered. Null only if the default itself
    /// has gone from the catalogue, which would be a mistake in this assembly rather than in a setting.
    /// </summary>
    public SpeechModel? SelectedModel =>
        SpeechModelCatalog.Find(_settings.Settings.Speech.ModelId) ??
        SpeechModelCatalog.Find(SpeechModelCatalog.DefaultModelId);

    /// <summary>The input devices the backend can see. Routed through here so Settings never has to
    /// open an audio backend of its own — the service owns the microphone, and a test that fakes it
    /// fakes this too.</summary>
    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => _capture.GetInputDevices(rescan);

    /// <summary>
    /// Whether dictation could actually start this instant: switched on, audio present, model on disk.
    /// <para>What the shortcut asks before claiming a key. The microphone button does not ask — clicking
    /// it is a request for the feature, and being told what is missing is the answer to that.</para>
    /// </summary>
    public bool IsReady
    {
        get
        {
            var speech = _settings.Settings.Speech;
            var model = SelectedModel;
            return speech.Enabled && IsAudioAvailable && model is not null && _store.IsDownloaded(model);
        }
    }

    /// <summary>
    /// Whether to put the "you have no model" question to the user on this start.
    /// </summary>
    /// <remarks>
    /// <para>Every fresh installation is in this state: dictation is on, no model ships with the
    /// application, and until one is downloaded the shortcut deliberately does nothing. Without asking,
    /// the feature exists but is invisible — the only hint is a microphone button that answers with a
    /// complaint.</para>
    /// <para>Not asked when dictation is switched off (they said no to the feature), when there is no
    /// audio backend at all (nothing to offer), when a model is already there, or when it has been asked
    /// before.</para>
    /// </remarks>
    public bool ShouldOfferModelDownload()
    {
        var speech = _settings.Settings.Speech;
        if (!speech.Enabled || speech.ModelPromptAnswered || !IsAudioAvailable)
            return false;

        return !SpeechModelCatalog.All.Any(_store.IsDownloaded);
    }

    /// <summary>Records that the question has been put, whatever the answer was.</summary>
    public void MarkModelPromptAnswered()
    {
        _settings.Settings.Speech.ModelPromptAnswered = true;
        _settings.NotifyChanged();
    }

    /// <summary>
    /// Drops the model from memory now, rather than when the idle timer gets round to it.
    /// </summary>
    /// <remarks>
    /// <para>Needed before deleting a model's files: while it is loaded the engine holds them open and
    /// the delete fails — on Windows, silently as far as the user can see.</para>
    /// <para><b>Blocks</b> for as long as a transcription is still using the engine, up to ten seconds.
    /// Never call it from the UI thread. It is safe to do so now only because the transcription pipeline
    /// no longer needs the dispatcher to finish — before that, blocking the UI thread here stopped the
    /// very continuation that would have released the engine, and the wait could only ever time out.</para>
    /// </remarks>
    /// <param name="onlyIfPath">
    /// When given, unloads only if this is the model that is actually loaded. Deleting one model used to
    /// drop whatever was resident, so removing a model the user had never dictated with cost the next
    /// dictation a two-second reload of a completely different one.
    /// </param>
    public void UnloadModel(string? onlyIfPath = null) => _engines.Unload(onlyIfPath);

    /// <summary>
    /// Starts recording for <paramref name="owner"/>, delivering the transcript to
    /// <paramref name="deliver"/> on the dispatcher thread.
    /// </summary>
    /// <param name="deliver">
    /// Puts the transcript somewhere, and says whether it managed to. False is not a detail to swallow:
    /// the tile it was meant for may have closed, or its shell may have exited, and the user has just
    /// spoken a paragraph into nothing. The service turns that into an <see cref="Error"/> rather than
    /// leaving the silence to be interpreted.
    /// </param>
    /// <returns>False when it could not start; <see cref="Error"/> has already said why.</returns>
    /// <remarks>
    /// <b>Blocks its caller while the microphone opens</b> — measured here at 208–301 ms, and 394 ms the
    /// first time, when the audio backend is initialised. Called from the UI thread (a button, a
    /// shortcut), so that is a visible stall at the moment of pressing, and the same quarter-second is
    /// missing from the front of the recording whichever thread does it. Deliberately not moved: opening
    /// the device off-thread would leave a window in which a stop can arrive before the stream exists,
    /// and it would buy no audio back. The fix that would is Handy's <c>always_on_microphone</c> —
    /// holding the stream open for as long as dictation is enabled — which is a decision about a live
    /// microphone in a terminal application, not one to slip in as a default.
    /// </remarks>
    public bool Start(object owner, Func<string, bool> deliver)
    {
        lock (_gate)
        {
            if (State != DictationState.Idle)
            {
                // Say which kind of busy. Pressing the key or the button again is the natural thing to
                // do when nothing has appeared yet, and silence reads as the feature being broken
                // rather than occupied — the microphone button in particular answers a click, always.
                Report(State == DictationState.Transcribing
                    ? "Still working on the previous recording."
                    : "Another tile is already recording.");
                return false;
            }

            // The window is closing. Opening a microphone now would open one nothing is left to close:
            // Dispose has already run, and the shortcut handler outlives it by however long the shutdown
            // takes.
            if (_disposed)
                return false;

            var speech = _settings.Settings.Speech;
            if (!speech.Enabled)
            {
                Report("Dictation is switched off. Turn it on in Settings → Speech.");
                return false;
            }

            if (!_capture.IsAvailable)
            {
                Report("No audio input is available on this machine.");
                return false;
            }

            var model = SelectedModel;
            if (model is null || !_store.IsDownloaded(model))
            {
                // Naming the model, because "no model has been downloaded" is a lie as soon as the user
                // has downloaded a different one — and that is the likeliest way to arrive here.
                Report(model is null
                    ? "No speech model is available. Settings → Speech has the list."
                    : $"The selected model ({model.Name}) is not on this machine. "
                      + "Download it in Settings → Speech, or pick one you already have.");
                return false;
            }

            try
            {
                _capture.Start(speech.InputDeviceName);
            }
            catch (AudioCaptureBusyException ex)
            {
                // Not a microphone problem: the capture still has a recording attached, which this
                // service's own state machine says is impossible. Blaming it on the hardware sent the
                // user off to check their device while the fault was here — and quietly, because
                // "the microphone could not be opened" is a sentence people believe.
                Trace.TraceError("Dictation asked for a microphone that is still busy: {0}", ex);
                Report("Dictation could not start: the previous recording has not finished closing. "
                    + "Try again in a moment.");
                return false;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Starting the microphone failed: {0}", ex);
                Report($"The microphone could not be opened: {ex.Message}");
                return false;
            }

            _deliver = deliver;
            _recordingStarted = Stopwatch.GetTimestamp();
            Owner = owner;
            SetState(DictationState.Recording);
            StartRecordingLimit();
            return true;
        }
    }

    /// <summary>Stops recording and transcribes what was captured. Safe to call when not recording.</summary>
    public void Stop()
    {
        Func<string, bool>? deliver;
        CancellationToken token;
        TimeSpan recorded;
        IRecordingHandle? detached;
        lock (_gate)
        {
            if (State != DictationState.Recording)
                return;

            deliver = _deliver;
            recorded = Stopwatch.GetElapsedTime(_recordingStarted);

            // Replaced, not disposed — the same rule as in Dispose, and for the same reason. The state
            // machine says the previous transcription has finished by now, but "should have finished"
            // is exactly the assumption that makes disposing a source whose token is still held turn
            // into an ObjectDisposedException somewhere else. A source with nothing registered on it is
            // a few bytes; a token that stops answering is a failure reported to the user.

            // Under the lock, before anything is handed to the thread pool: the microphone has to be
            // free the instant this method returns, or a user who stops and immediately starts again
            // gets a capture that is still "recording" and silently refuses to open a stream.
            detached = _capture.Detach();
            _work = new CancellationTokenSource();
            token = _work.Token;
            _recordingLimit?.Dispose();
            _recordingLimit = null;
            SetState(DictationState.Transcribing);
        }

        // Closing the stream takes 50–150 ms, and up to two seconds when the consumer has to drain — on
        // the UI thread that is a freeze at the end of *every* dictation, right where the user expects
        // their words to appear. The capture's own state is per-recording, so a stop still finishing
        // while the next one starts cannot corrupt either.
        _ = Task.Run(async () => await TranscribeAsync(SafeFinishCapture(detached), recorded, deliver, token));
    }

    /// <summary>Throws away the recording, or the transcription in flight. The tile gets nothing.</summary>
    public void Cancel()
    {
        IRecordingHandle? detached = null;
        lock (_gate)
        {
            if (State == DictationState.Idle)
                return;

            _deliver = null;
            _work?.Cancel();

            if (State == DictationState.Recording)
            {
                detached = _capture.Detach();
                SetState(DictationState.Idle);
            }

            _recordingLimit?.Dispose();
            _recordingLimit = null;
        }

        // Off the thread entirely, exactly as Stop does it: cancelling is meant to feel instant, and
        // closing the stream is 50–150 ms of native work with a two-second worst case behind it. The
        // recording is already detached, so pressing the key again right now opens a new stream rather
        // than colliding with this one.
        if (detached is not null)
            _ = Task.Run(() => SafeFinishCapture(detached));
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= RaiseStateChanged;

        IRecordingHandle? detached;
        lock (_gate)
        {
            _disposed = true;

            // Cancelled, and deliberately **not** disposed. A transcription may be inside the engine
            // right now holding this token, and disposing a source whose token is still in use turns
            // every later use of it into an ObjectDisposedException — which arrives in the pipeline's
            // catch-all and is announced to the user as "Transcription failed: …" as the window closes.
            // The source is a handful of bytes on a process that is ending; the dialog is not.
            _work?.Cancel();

            detached = _capture.Detach();
            _engines.CancelScheduledUnload();
            _recordingLimit?.Dispose();
            _recordingLimit = null;

            // Whatever it was doing, it is not doing it any more. The transcription that was running
            // will find its own way out through the cancelled token; nothing should read Recording off
            // a service that has been disposed.
            SetState(DictationState.Idle);
        }

        // The microphone goes on the thread pool, like every other route out of a recording: this runs
        // on the UI thread with the window closing, and closing a stream waits up to two seconds for the
        // consumer to drain. Nobody is waiting for those samples — the tile they were meant for is gone
        // — so a two-second hang on exit would buy precisely nothing, and if the process ends first the
        // device is released more thoroughly than this could manage. Guarded, like every other shutdown
        // step here: a microphone that refuses to close must not cost the steps after it.
        _ = Task.Run(() =>
        {
            SafeFinishCapture(detached);
            try { _capture.Dispose(); }
            catch (Exception ex) { Trace.TraceWarning("Closing the microphone failed: {0}", ex); }
        });

        // The model, the engine holding it and the timer that would have dropped it: one object, and it
        // knows not to wait for a transcription while the window is closing.
        _engines.Dispose();
    }

    /// <param name="recorded">How long the microphone was open. The one thing that separates a stray tap
    /// on the shortcut from a key held down and spoken into, which is what decides whether a capture that
    /// produced no samples at all is worth complaining about.</param>
    private async Task TranscribeAsync(float[] samples, TimeSpan recorded, Func<string, bool>? deliver,
        CancellationToken token)
    {
        var speech = _settings.Settings.Speech;
        var deliberate = recorded >= TimeSpan.FromSeconds(1);
        try
        {
            if (samples.Length == 0)
            {
                // Held the key for a second and got not one sample: the microphone is not working, and
                // that is worth saying. A tap that caught nothing is just a tap.
                if (deliberate)
                    Report("The microphone produced no audio.");
                return;
            }

            var model = SelectedModel
                ?? throw new InvalidOperationException("No speech model is selected.");

            var options = new TranscriptionOptions(
                speech.Language,
                speech.TranslateToEnglish,
                speech.CustomWords.Count > 0 ? string.Join(", ", speech.CustomWords) : null);

            // Loading and transcribing are one indivisible use of the engine — the idle timer must not
            // be able to unload a model between them, nor while the native code is mid-inference — which
            // is why they are one call on the host rather than two from here.
            var raw = await _engines
                .TranscribeAsync(model.Kind, _store.GetPath(model), Pad(samples), options, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // Evidence about the language, not a preference. Whisper is *told* the setting and decodes
            // in it, so for whisper the setting is the answer. Parakeet detects for itself and never
            // says which it chose — and the Settings tab hides the language control when Parakeet is
            // selected, so whatever is stored there is not something the user said about this
            // transcript. Unknown, and the cleaner then removes only what is not a word in any language.
            var spoken = model.HasWhisperOnlyOptions ? speech.Language : "auto";
            var text = TranscriptPostProcessor.Clean(raw, spoken, speech.RemoveFillerWords);
            if (text.Length == 0)
            {
                // Silently. A modal saying "nothing recognisable was heard" is a dialog demanding a
                // click in exchange for no information: the tile's border already showed the recording
                // and the transcription, so nothing arriving where the words should have gone says the
                // same thing without interrupting. Nothing is broken in this case either — the engine
                // ran and had nothing to report, which is what a pause or a cough sounds like.
                // The empty *capture* above is a different matter and still speaks up: that one means
                // the microphone delivered nothing at all, which is a fault rather than a result.
                Trace.WriteLine("[speech] the transcript was empty");
                return;
            }

            if (deliver is null)
                return;

            _dispatch(() =>
            {
                // Asked again here, on the dispatcher. Cancel sets the flag and moves on, so between the
                // check above and this callback actually running there is a window in which Escape has
                // been pressed and the words would be typed anyway — into a tile the user has already
                // told us to leave alone.
                if (token.IsCancellationRequested)
                    return;

                // The callback belongs to a tile and touches its terminal, a text box, a document — any
                // of which can be mid-teardown by now. This runs on the dispatcher, so an exception here
                // is an unhandled exception on the UI thread, which takes the application down over one
                // undelivered sentence. A throw means the same thing to the user as false: the words did
                // not arrive.
                var delivered = false;
                try
                {
                    delivered = deliver(text);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Delivering a transcript failed: {0}", ex);
                }

                if (!delivered)
                    Error?.Invoke($"There was nowhere to put the text: \"{Shorten(text)}\"");
            });
        }
        catch (OperationCanceledException)
        {
            // The user pressed Escape, or started again. Nothing to say.
        }
        catch (ObjectDisposedException)
        {
            // The same thing wearing a different exception: something the transcription was holding has
            // been taken away underneath it, which only happens on the way out. Nobody is waiting for
            // these words, and a dialog about it would be the last thing on screen before the window
            // disappears.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Transcription failed: {0}", ex);
            Report($"Transcription failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _deliver = null;
                SetState(DictationState.Idle);
            }
            ScheduleUnload();
        }
    }

    /// <summary>
    /// Pads a very short recording out to 1.25 s.
    /// <para>Whisper is trained on 30 s windows and produces nonsense from a fragment barely longer than
    /// its own frame; Handy pads for the same reason (<c>managers/audio.rs</c>). A tap on the key is a
    /// tap, not an error, so this is padding rather than rejection.</para>
    /// </summary>
    internal static float[] Pad(float[] samples)
    {
        const int minimum = IAudioCapture.SampleRate;             // 1 s
        const int target = IAudioCapture.SampleRate * 5 / 4;      // 1.25 s

        if (samples.Length is 0 or >= minimum)
            return samples;

        var padded = new float[target];
        samples.CopyTo(padded, 0);
        return padded;
    }

    private float[] SafeFinishCapture(IRecordingHandle? detached)
    {
        try
        {
            return _capture.Finish(detached);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Stopping the microphone failed: {0}", ex);
            return [];
        }
    }

    /// <summary>Enough of the transcript to recognise it by, so a failure message hands the words back
    /// rather than only reporting that they are gone.</summary>
    private static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..57] + "…";

    private void SetState(DictationState state)
    {
        State = state;
        if (state == DictationState.Idle)
            Owner = null;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => _dispatch(() => StateChanged?.Invoke());

    private void Report(string message) => _dispatch(() => Error?.Invoke(message));

    /// <summary>
    /// How long a single recording may run before it is stopped and transcribed anyway.
    /// </summary>
    /// <remarks>
    /// Push-to-talk ends when the key comes up, but toggle mode ends only when somebody presses again —
    /// and a recording nobody stops grows at 64 KB a second and ends in a transcription of an hour of
    /// audio. Five minutes is far more than anyone dictates into a prompt in one go. It <em>stops</em>
    /// rather than discards: the words already spoken are still worth having.
    /// </remarks>
    private static readonly TimeSpan DefaultMaxRecording = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _maxRecording;

    /// <summary>When set, replaces the settings-derived idle period. Tests only — the setting is in
    /// whole minutes, and a minute is not a unit a test suite can spend.</summary>
    private readonly TimeSpan? _unloadAfter;

    private void StartRecordingLimit()
    {
        _recordingLimit?.Dispose();
        _recordingLimit = new Timer(_ =>
        {
            try
            {
                if (State != DictationState.Recording)
                    return;

                Trace.TraceWarning("Dictation reached its {0:0.##}-minute limit; transcribing what there is.",
                    _maxRecording.TotalMinutes);

                // Said out loud, not only to the log. Everything else that ends a recording is something
                // the user did, so a transcript appearing is confirmation; this one ends by itself, and
                // what arrives is the first few minutes of a longer sentence with nothing to say where
                // it was cut. A toggle-mode recording forgotten about is exactly how somebody gets here.
                Report($"Recording stopped at the {_maxRecording.TotalMinutes:0.##}-minute limit. "
                    + "What was said up to then has been transcribed.");
                Stop();
            }
            catch (Exception ex)
            {
                // A thread-pool thread: an escaping exception here would end the process.
                Trace.TraceWarning("Stopping an over-long recording failed: {0}", ex);
            }
        }, null, _maxRecording, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Drops the model after the configured idle period. Zero minutes means never.</summary>
    private void ScheduleUnload()
    {
        // Two values, and they answer different questions, which is why collapsing them into one test on
        // the delay is wrong: the **setting** says whether the model is ever dropped — zero means never,
        // and that is a choice the user makes — while `_unloadAfter` only shortens the wait so a test
        // does not sit through half an hour. Timing by the seam and deciding by the seam are not the
        // same thing: it made "zero means never" untestable, because the seam that shortens the wait
        // also overruled the setting under test.
        var minutes = _settings.Settings.Speech.ModelUnloadMinutes;

        // Clamped to the same maximum the control in Settings offers — see SpeechSettings.MaxUnloadMinutes
        // for why there is a maximum at all, and why it is one number rather than two.
        var after = _unloadAfter ?? TimeSpan.FromMinutes(Math.Min(minutes, SpeechSettings.MaxUnloadMinutes));

        if (minutes <= 0 || _disposed)
        {
            _engines.CancelScheduledUnload();
            return;
        }

        _engines.ScheduleUnload(after);
    }
}
