namespace mTiles.Services.Speech;

/// <summary>
/// The one place that knows which engine runs which kind of model, and what that kind looks like on
/// disk.
/// </summary>
/// <remarks>
/// <para>Both questions were answered where they were asked: the service built the engine with a test on
/// <c>SpeechModel.Kind</c>, and the store asked <c>ParakeetSpeechEngine</c> directly whether an unpacked
/// directory was complete. Neither was wrong — the store's check in particular exists so that "downloaded"
/// and "loadable" cannot drift apart — but it left the store, which is about files and HTTP, naming an
/// engine class, and it left a third engine needing edits in places nobody would think to look.</para>
/// <para>A registry rather than an interface with a factory: the set of kinds is closed and defined in
/// this assembly, and the alternative buys nothing but a level of indirection over a two-armed switch.
/// What it does buy is that the switch happens once.</para>
/// </remarks>
internal static class SpeechEngines
{
    /// <summary>An engine that can run this kind of model. Nothing is loaded until it is asked to.</summary>
    public static ISpeechToTextEngine Create(SpeechModelKind kind) => kind switch
    {
        SpeechModelKind.ParakeetOnnx => new ParakeetSpeechEngine(),
        _ => new WhisperSpeechEngine(),
    };

    /// <summary>
    /// Whether what is on disk at <paramref name="path"/> is a model the matching engine could load.
    /// </summary>
    /// <remarks>
    /// The archive case is the one that matters: an extraction stopped halfway leaves a directory that
    /// exists and holds a graph or two, and calling that downloaded arms the shortcut and defers the
    /// failure to the moment somebody has already spoken. The list of files comes from the engine's own
    /// loader, so the two cannot disagree. A single-file model is judged by its published size — the
    /// digest was checked when it was downloaded, and hashing half a gigabyte to draw a checkmark is not
    /// worth it.
    /// </remarks>
    public static bool IsComplete(SpeechModel model, string path) => model.Kind switch
    {
        SpeechModelKind.ParakeetOnnx => ParakeetSpeechEngine.HasRequiredFiles(path),
        _ => new FileInfo(path) is { Exists: true } file && file.Length == model.DownloadBytes,
    };
}
