using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace mTiles.Services.Speech;

/// <summary>
/// whisper.cpp, through Whisper.net — the engine Handy transcribes with, taking the same ggml models.
/// </summary>
/// <remarks>
/// The factory (the loaded model, hundreds of megabytes) is kept between transcriptions; the processor
/// is built per utterance, because language and prompt are settings the user can change between one
/// and the next.
/// </remarks>
internal sealed class WhisperSpeechEngine : ISpeechToTextEngine
{
    private readonly Lock _gate = new();
    private WhisperFactory? _factory;
    private string? _loadedPath;

    public bool IsLoaded
    {
        get { lock (_gate) return _factory is not null; }
    }

    /// <summary>The model file currently in memory, or null.</summary>
    public string? LoadedPath
    {
        get { lock (_gate) return _loadedPath; }
    }

    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // The same comparison the service uses to decide whether the model being deleted is the one
            // that is loaded (FileHelper.SamePath). Two spellings of "is this the same file?" in one
            // feature is how one of them ends up reloading half a gigabyte over a difference in case.
            if (FileHelper.SamePath(_loadedPath, modelPath) && _factory is not null)
                return Task.CompletedTask;
        }

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech model file is missing.", modelPath);

        return Task.Run(() =>
        {
            var factory = WhisperFactory.FromPath(modelPath);
            WhisperFactory? previous;
            lock (_gate)
            {
                previous = _factory;
                _factory = factory;
                _loadedPath = modelPath;
            }
            previous?.Dispose();
            Trace.WriteLine($"[speech] loaded model {Path.GetFileName(modelPath)}");
        }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only while nothing is transcribing — see <see cref="ISpeechToTextEngine.Unload"/>. The lock here
    /// covers taking the factory out, not the inference that may still be holding it: whisper.cpp is
    /// native, and disposing the factory under it is an access violation rather than an exception.
    /// </remarks>
    public void Unload()
    {
        WhisperFactory? factory;
        lock (_gate)
        {
            factory = _factory;
            _factory = null;
            _loadedPath = null;
        }

        factory?.Dispose();
        if (factory is not null)
            Trace.WriteLine("[speech] model unloaded");
    }

    public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        WhisperFactory factory;
        lock (_gate)
        {
            factory = _factory ?? throw new InvalidOperationException("No speech model is loaded.");
        }

        var builder = factory.CreateBuilder()
            .WithThreads(RecommendedThreads());

        if (string.Equals(options.Language, "auto", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithLanguageDetection();
        else
            builder = builder.WithLanguage(options.Language);

        if (options.TranslateToEnglish)
            builder = builder.WithTranslate();

        if (!string.IsNullOrWhiteSpace(options.Prompt))
            builder = builder.WithPrompt(options.Prompt);

        await using var processor = builder.Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            text.Append(segment.Text);

        return text.ToString().Trim();
    }

    public void Dispose() => Unload();

    /// <summary>
    /// Leaves a core free. Dictation runs while the user is working, and a machine that stops repainting
    /// for the length of a transcription is worse than a transcription that takes a second longer.
    /// </summary>
    private static int RecommendedThreads() => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
}
