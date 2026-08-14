namespace mTiles.Services.Speech;

/// <summary>What the engine is asked for, beyond the audio itself.</summary>
/// <param name="Language">ISO-639-1 code, or <c>auto</c> to let the model decide.</param>
/// <param name="TranslateToEnglish">Ask for English regardless of what was spoken.</param>
/// <param name="Prompt">
/// Words the decoder should expect — names, jargon, project vocabulary. Whisper takes this as its
/// initial prompt, which biases recognition instead of correcting the text afterwards. Parakeet takes
/// no prompt and ignores it, which is why the Settings tab hides the field when Parakeet is chosen.
/// </param>
internal readonly record struct TranscriptionOptions(
    string Language = "auto",
    bool TranslateToEnglish = false,
    string? Prompt = null);

/// <summary>
/// One utterance of 16 kHz mono audio in, one string out.
/// </summary>
/// <remarks>
/// The interface exists so a second engine can be added without the rest of the feature noticing.
/// Two implementations sit behind it — <see cref="ParakeetSpeechEngine"/> on ONNX Runtime, which is the
/// default, and <see cref="WhisperSpeechEngine"/> on whisper.cpp — chosen per model by
/// <c>SpeechModel.Kind</c>. Handy does the same thing for the same reason: no one engine reads every
/// model worth offering.
/// </remarks>
internal interface ISpeechToTextEngine : IDisposable
{
    /// <summary>Whether a model is currently held in memory.</summary>
    bool IsLoaded { get; }

    /// <summary>Reads a model into memory. Slow and allocation-heavy; call it off the UI thread.</summary>
    Task LoadAsync(string modelPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the model. The next transcription loads it again.
    /// </summary>
    /// <remarks>
    /// <para><b>Only when nothing is transcribing.</b> Both implementations take the loaded model out
    /// from under a lock and then work on it outside one, which is what lets a transcription run while
    /// the tab asks whether a model is loaded — and it means an <see cref="Unload"/> racing an inference
    /// frees native memory the native code is still reading. That is an access violation, which takes
    /// the process down rather than arriving as an exception anybody can catch.</para>
    /// <para>The invariant is kept by the one caller, <see cref="SpeechEngineHost"/>, which serialises
    /// loading, transcribing and unloading on a semaphore of its own — including its idle timer, which
    /// takes that semaphore with a zero timeout and gives up rather than waiting. It is written here as
    /// well because the class that would crash is not the class that holds the rule.</para>
    /// </remarks>
    void Unload();

    /// <param name="samples">16 kHz mono samples in [-1,1].</param>
    /// <returns>The transcript, unprocessed — cleaning it up is not the engine's business.</returns>
    Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
        CancellationToken cancellationToken = default);
}
