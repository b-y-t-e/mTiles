using mTiles.Services.Speech;

namespace mTiles.Tests;

/// <summary>A microphone that records nothing, for a dictation service whose audio is not the point.
/// </summary>
/// <remarks>It does keep the one bit of state a recording has — started or not — so a test can ask whether
/// a tile's recording was stopped.</remarks>
internal sealed class SilentAudioCapture : IAudioCapture
{
    private sealed record Handle : IRecordingHandle;

    public bool IsAvailable => true;
    public bool IsRecording { get; private set; }
    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["fake microphone"];
    public void Start(string deviceName) => IsRecording = true;

    public IRecordingHandle? Detach()
    {
        if (!IsRecording) return null;
        IsRecording = false;
        return new Handle();
    }

    public float[] Finish(IRecordingHandle? detached) => [];
    public void Dispose() { }
}

/// <summary>A speech engine that loads nothing and hears nothing.</summary>
internal sealed class SilentSpeechEngine : ISpeechToTextEngine
{
    public bool IsLoaded => false;
    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Unload() { }
    public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
        CancellationToken cancellationToken = default) => Task.FromResult("");
    public void Dispose() { }
}
