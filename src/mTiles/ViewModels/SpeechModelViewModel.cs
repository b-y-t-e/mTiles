using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

/// <summary>
/// One row in the model list: what it is, whether it is on this machine, and the download in progress.
/// </summary>
public sealed partial class SpeechModelViewModel : ObservableObject
{
    private readonly SpeechModelStore _store;
    private CancellationTokenSource? _download;

    internal SpeechModelViewModel(SpeechModel model, SpeechModelStore store, bool isSelected)
    {
        Model = model;
        _store = store;
        _isSelected = isSelected;
        _isDownloaded = store.IsDownloaded(model);
    }

    internal SpeechModel Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Note => Model.Note;
    public string SizeText => $"{Model.SizeMegabytes:0} MB";

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether this is the model dictation would actually use. Chosen <em>and</em> present: a chosen
    /// model whose file has just been deleted is not in use, and saying so beside a Download button
    /// reads as a contradiction.
    /// </summary>
    public bool IsInUse => IsSelected && IsDownloaded;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(IsInUse));
    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(IsInUse));

    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>0 to 1 while downloading.</summary>
    [ObservableProperty]
    private double _progress;

    public string ProgressText => $"{Progress * 100:0}%";

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));

    [ObservableProperty]
    private string? _error;

    /// <summary>Raised when the user picks this model; the settings view model stores the choice.</summary>
    internal Action<SpeechModelViewModel>? SelectRequested { get; set; }

    /// <summary>Asks the user before something irreversible. Wired from the view, like every other
    /// destructive action in this application.</summary>
    internal Func<string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>
    /// What a delete says when there is no way to ask the question.
    /// </summary>
    /// <remarks>
    /// Said in two places — here, when nothing wired a dialog at all, and in the settings tab, when what
    /// it wired has nothing to show a dialog in. One sentence, because they are the same answer to the
    /// same question and the user cannot tell the two apart.
    /// </remarks>
    internal const string NothingToConfirmWith =
        "Deleting is unavailable: nothing is here to confirm it with.";

    /// <summary>Drops the model from memory, so its files can be deleted. Supplied by the settings view
    /// model, which is the one that knows about the dictation service.</summary>
    internal Action? ReleaseModel { get; set; }

    /// <summary>Raised whenever this model appears on disk or leaves it, so the tab can settle on a model
    /// that actually exists. Only the tab can tell — it is the one that sees the other rows.</summary>
    internal Action<SpeechModelViewModel?>? AvailabilityChanged { get; set; }

    /// <summary>Asks the disk again. A download in progress is left alone — it is about to answer this
    /// itself, and a half-written file says nothing useful in the meantime.</summary>
    internal void RefreshDownloaded()
    {
        if (!IsDownloading)
            IsDownloaded = _store.IsDownloaded(Model);
    }

    [RelayCommand]
    private void Select() => SelectRequested?.Invoke(this);

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading)
            return;

        Error = null;
        Progress = 0;
        IsDownloading = true;
        _download = new CancellationTokenSource();

        try
        {
            // The same reason deleting unloads first, and the same asymmetry it would otherwise be:
            // downloading a model that is somehow loaded ends by replacing the very files the engine has
            // open — File.Move over a ggml file, or a recursive delete of a Parakeet directory before the
            // unpacked one is moved into place — and Windows refuses. This only arises when a downloaded
            // model stops counting as downloaded while it is loaded (a file removed from under us), which
            // is exactly the state somebody arrives in when they are trying to repair a bad model.
            // Scoped to this model, so downloading a second one never unloads the one in use.
            await Task.Run(() => ReleaseModel?.Invoke());

            // Progress<T> is created here, on the UI thread, so its callbacks come back to it.
            await _store.DownloadAsync(Model, new Progress<double>(v => Progress = v), _download.Token);
            IsDownloaded = _store.IsDownloaded(Model);

            // Downloading a model the app then refuses to use is a dead end: the shortcut stays inert
            // and the microphone button reports a missing model while this one sits on disk. So if
            // nothing usable is selected, the thing just downloaded becomes the selection.
            if (IsDownloaded)
                AvailabilityChanged?.Invoke(this);
        }
        catch (OperationCanceledException)
        {
            // The partial file stays; the next attempt resumes from it.
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _download?.Dispose();
            _download = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    /// <summary>
    /// Removes the model from disk, having asked first.
    /// </summary>
    /// <remarks>
    /// <para>Asking, because this throws away a download measured in hundreds of megabytes and, on a
    /// slow connection, in hours — the same reason every other destructive action in this application
    /// confirms.</para>
    /// <para>Unloading first, because a model in memory has its files open and the delete fails; and
    /// reporting the failure, because a button that silently does nothing is worse than one that
    /// refuses. Off the UI thread, like the download: 640 MB across several files is not an instant
    /// delete on every disk.</para>
    /// </remarks>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        // No confirmation wired means no delete. Every other destructive action in this application
        // proceeds when nobody supplied a dialog, and for a click that discards hundreds of megabytes
        // and hours of somebody's connection that default is the wrong way round. Saying so rather than
        // failing quietly, because a button that does nothing without explanation is its own bug.
        if (ConfirmAction is null)
        {
            Error = NothingToConfirmWith;
            return;
        }

        if (!await ConfirmAction($"Delete {Name} ({SizeText}) from disk?"))
            return;

        Error = null;

        // Both on the thread pool. Releasing the model waits for any transcription to finish with it,
        // which is up to ten seconds — the delete was already off the UI thread and the wait in front of
        // it would have put the freeze straight back.
        var deleted = await Task.Run(() =>
        {
            ReleaseModel?.Invoke();
            return _store.Delete(Model);
        }).ConfigureAwait(true);
        IsDownloaded = _store.IsDownloaded(Model);
        Progress = 0;

        // The other half of adopting a download: deleting the model in use leaves the selection pointing
        // at nothing, and dictation would report a missing model with another one sitting on disk.
        if (!IsDownloaded)
            AvailabilityChanged?.Invoke(null);

        if (!deleted)
            Error = "The files could not be removed — a download of this model may still be running, "
                + "or something else has them open.";
    }
}
