using mTiles.Services.Phone;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A microphone that records what a test says it heard: <see cref="Samples"/>, a second of silence unless
/// told otherwise.
/// </summary>
/// <remarks>Detaching is instant and finishing hands back the samples, the two halves the real capture
/// has. It refuses a second start while a recording is still attached, as the real one does — a service
/// that asks for one is the bug this exists to catch.</remarks>
internal sealed class FakeMicrophone : IAudioCapture
{
    private sealed record Handle(float[] Samples) : IRecordingHandle;

    public bool IsAvailable { get; set; } = true;
    public bool IsRecording { get; private set; }
    public float[] Samples { get; set; } = new float[16_000];
    public IReadOnlyList<string> Devices { get; set; } = ["Yeti"];

    /// <summary>Whether any recording was ever started on this microphone.</summary>
    public bool Started { get; private set; }

    /// <summary>How many recordings were finished — the slow half, run off the caller's thread.</summary>
    public int StopCount { get; private set; }

    /// <summary>Set to make the next start fail, as a device that is busy or gone would.</summary>
    public bool FailNextStart { get; set; }

    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => Devices;

    public void Start(string deviceName)
    {
        Assert.False(IsRecording, "started a recording while the previous one was still attached");

        if (FailNextStart)
        {
            // The real one leaves nothing behind when this happens, so the next attempt may try again.
            FailNextStart = false;
            throw new InvalidOperationException("the device could not be opened");
        }

        Started = true;
        IsRecording = true;
    }

    public IRecordingHandle? Detach()
    {
        if (!IsRecording)
            return null;

        IsRecording = false;
        return new Handle(Samples);
    }

    public float[] Finish(IRecordingHandle? detached)
    {
        if (detached is not Handle handle)
            return [];

        StopCount++;
        return handle.Samples;
    }

    public void Dispose() { }
}

/// <summary>A microphone that is there (or says it is not) and never records anything.</summary>
internal sealed class IdleMicrophone : IAudioCapture
{
    public bool IsAvailable { get; init; } = true;
    public bool IsRecording => false;
    public IReadOnlyList<string> Devices { get; set; } = [];

    public IReadOnlyList<string> GetInputDevices(bool rescan = false) => Devices;
    public void Start(string deviceName) { }
    public IRecordingHandle? Detach() => null;
    public float[] Finish(IRecordingHandle? detached) => [];
    public void Dispose() { }
}

/// <summary>A speech engine that hears <see cref="Transcript"/> and counts what was asked of it.</summary>
internal sealed class FakeSpeechEngine : ISpeechToTextEngine
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Transcript { get; set; } = "said something";
    public bool IsLoaded { get; private set; }
    public int Loads { get; private set; }
    public int Calls { get; private set; }

    /// <summary>Set to hold every transcription until <see cref="Release"/>.</summary>
    public bool BlockUntilReleased { get; set; }

    public Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (!IsLoaded)
            Loads++;
        IsLoaded = true;
        return Task.CompletedTask;
    }

    public void Unload() => IsLoaded = false;

    public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (BlockUntilReleased)
            await _release.Task.WaitAsync(cancellationToken);
        return Transcript;
    }

    public void Release() => _release.TrySetResult();

    public void Dispose() { }
}

/// <summary>Runs the work inline: there is no UI thread in these tests and nothing needs one.</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
    public Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
}

/// <summary>Keeps paired devices nowhere, so a test never writes into the running user's profile.</summary>
internal sealed class NowherePhoneSessionStore : IPhoneSessionStore
{
    public IReadOnlyList<PhoneSession> Load() => [];
    public void Save(IReadOnlyList<PhoneSession> sessions) { }
}

internal static class SpeechModelFiles
{
    /// <summary>Puts a model "on disk" in <paramref name="directory"/>: a sparse file of exactly the
    /// downloaded size, which is all <see cref="SpeechModelStore.IsDownloaded"/> asks for. A single-file
    /// model by default, so this is one <c>SetLength</c> rather than an archive.</summary>
    public static SpeechModel PlaceOnDisk(string directory, string modelId = "base")
    {
        var model = SpeechModelCatalog.Find(modelId)!;
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, model.FileName));
        file.SetLength(model.DownloadBytes);
        return model;
    }
}
