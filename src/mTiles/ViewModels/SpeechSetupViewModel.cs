using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

/// <summary>
/// Setting dictation up, once, in the order the pieces depend on each other: a model, a microphone, and
/// a sentence to prove the two.
/// </summary>
/// <remarks>
/// <para>Shown on a first run, where nothing is set up, and from Settings → Speech, where it is how you
/// start over. The same object both times: two windows saying the same thing is two windows to keep in
/// step, and the first-run one used to be a single screen that asked about the model and nothing else —
/// it could not tell anybody whether dictation actually worked.</para>
/// <para>It owns no state of its own beyond the step it is on. The model, the device and the transcript
/// all live where they already lived: the catalogue, the settings file, the dictation service.</para>
/// </remarks>
public sealed partial class SpeechSetupViewModel : ObservableObject, IDisposable
{
    private readonly DictationService _dictation;
    private readonly SettingsService _settings;

    internal SpeechSetupViewModel(DictationService dictation, SettingsService settings)
    {
        _dictation = dictation;
        _settings = settings;

        var chosen = settings.Settings.Speech.ModelId;

        // The recommended few, plus whatever the user is actually using if it is not among them. The
        // list is editorial — three of six — so somebody who chose Whisper Large in Settings and then
        // opens this window would otherwise be shown three models, none of them theirs.
        var offered = SpeechModelCatalog.Recommended
            .Concat(SpeechModelCatalog.All.Where(m =>
                m.Id == chosen && !SpeechModelCatalog.Recommended.Contains(m)));

        foreach (var model in offered)
        {
            var row = new SpeechModelViewModel(model, dictation.Store, model.Id == chosen)
            {
                SelectRequested = Choose,
                AvailabilityChanged = _ => RefreshModelReady(),
                // Wired here as well as in the Speech tab: a row that cannot unload its own model cannot
                // replace its files either, and which of the two lists the click came from is not
                // something the download should depend on.
                ReleaseModel = () => dictation.UnloadModel(dictation.Store.GetPath(model)),
            };
            Models.Add(row);
        }

        // Whatever is already usable is what the wizard opens on, so somebody re-running it does not
        // have to choose again to get past the first step.
        //
        // The *field*, not the property: setting the property runs Choose, which writes the model to
        // settings. Opening this window is not choosing anything, and on a configuration the list cannot
        // represent — a model that is downloaded but not offered here — the fallback to "the first row"
        // would silently replace a working setting with one whose file is not even on the machine. A
        // window that breaks dictation by being opened is the opposite of what it is for.
        _selected = Models.FirstOrDefault(m => m.IsInUse)
            ?? Models.FirstOrDefault(m => m.IsDownloaded)
            ?? Models.FirstOrDefault();

        RefreshModelReady();

        // What was working when this opened, so that closing half-way through cannot take it away.
        _workingAtOpen = IsModelReady ? settings.Settings.Speech.ModelId : null;

        // Adopting the preselection is right only when there is nothing to lose: no model configured, or
        // one configured whose file is not on the machine. Then a row is highlighted and the setting
        // agrees with it, which is what the step is for. When the configuration already works, the
        // highlight is a highlight and nothing is written.
        if (!IsModelReady && _selected is { } preselected)
            Choose(preselected);

        _device = string.IsNullOrEmpty(settings.Settings.Speech.InputDeviceName)
            ? SettingsViewModel.DefaultDeviceOption
            : settings.Settings.Speech.InputDeviceName;

        _dictation.StateChanged += OnDictationChanged;

        // While this window is up it is the one that has to say what went wrong. The application's own
        // handler puts these in a message box owned by the main window — behind a modal wizard, where
        // nobody can see or dismiss it — so a failed test step would look like nothing happening.
        _dictation.Error += OnDictationError;
    }

    private void OnDictationError(string message) => Message = message;

    /// <summary>
    /// The model that was configured and present when this window opened, or null if none was.
    /// </summary>
    /// <remarks>
    /// Picking a model in the list writes it to settings immediately — which is what makes the list a
    /// choice rather than a form to submit — and the first step then holds the user there until it is
    /// downloaded. Closing the window at that point used to leave dictation configured for a model that
    /// is not on the machine: a working setup broken by opening a window and thinking better of it. The
    /// choice is kept when there was nothing working to go back to, because then it is the only answer
    /// there is; and the download itself is kept either way, in its <c>.partial</c> file.
    /// </remarks>
    private readonly string? _workingAtOpen;

    private bool _disposed;

    public ObservableCollection<SpeechModelViewModel> Models { get; } = [];

    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty]
    private SpeechSetupStep _step = SpeechSetupStep.Model;

    [ObservableProperty]
    private SpeechModelViewModel? _selected;

    [ObservableProperty]
    private string _device;

    /// <summary>What came back from the microphone in the last step. Empty until something does.</summary>
    [ObservableProperty]
    private string _transcript = "";

    /// <summary>Anything the user needs told: no model, a busy microphone, a failed transcription.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTranscribing;

    /// <summary>True when the chosen model is on this machine — the one condition that blocks the first
    /// step, and the reason the other two exist.</summary>
    [ObservableProperty]
    private bool _isModelReady;

    public bool IsModelStep => Step == SpeechSetupStep.Model;
    public bool IsMicrophoneStep => Step == SpeechSetupStep.Microphone;
    public bool IsTestStep => Step == SpeechSetupStep.Test;

    public string StepCaption => $"Step {SpeechSetupFlow.NumberOf(Step)} of {SpeechSetupFlow.Steps.Count}";
    public bool CanGoBack => SpeechSetupFlow.Previous(Step) is not null;
    public bool CanGoNext => SpeechSetupFlow.CanLeave(Step, IsModelReady);
    public string NextCaption => SpeechSetupFlow.IsLast(Step) ? "Done" : "Next";

    /// <summary>Raised when the wizard is finished with — the view closes the window.</summary>
    public event Action? CloseRequested;

    partial void OnStepChanged(SpeechSetupStep value)
    {
        OnPropertyChanged(nameof(IsModelStep));
        OnPropertyChanged(nameof(IsMicrophoneStep));
        OnPropertyChanged(nameof(IsTestStep));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextCaption));
        Message = null;

        if (value == SpeechSetupStep.Microphone)
            _ = SafeLoadDevicesAsync();
    }

    partial void OnIsModelReadyChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    partial void OnSelectedChanged(SpeechModelViewModel? value)
    {
        if (value is not null)
            Choose(value);
    }

    partial void OnDeviceChanged(string value)
    {
        // An empty selection is the combo box rebuilding its items, not a choice — the same trap the
        // settings tab has, and the same answer.
        if (string.IsNullOrEmpty(value))
            return;

        _settings.Settings.Speech.InputDeviceName =
            value == SettingsViewModel.DefaultDeviceOption ? "" : value;
        _settings.NotifyChanged();
    }

    private void Choose(SpeechModelViewModel row)
    {
        foreach (var model in Models)
            model.IsSelected = ReferenceEquals(model, row);

        _settings.Settings.Speech.ModelId = row.Id;
        _settings.NotifyChanged();
        RefreshModelReady();
    }

    /// <summary>
    /// Whether the model dictation would actually use is on this machine.
    /// </summary>
    /// <remarks>
    /// Asked of the service rather than of the rows: <c>SelectedModel</c> is the setting, or the
    /// catalogue's default when the setting names nothing usable, and that is what a recording will try
    /// to load. Counting rows instead made the wizard block on its first step for somebody whose chosen
    /// model is downloaded and working but not one of the three it offers.
    /// </remarks>
    private void RefreshModelReady()
    {
        foreach (var row in Models)
            row.RefreshDownloaded();

        IsModelReady = _dictation.SelectedModel is { } model && _dictation.Store.IsDownloaded(model);
    }

    /// <summary>Fetches the model the user picked. Progress and cancelling belong to the row itself.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selected is not { IsDownloading: false, IsDownloaded: false } row)
            return;

        Message = null;
        await row.DownloadCommand.ExecuteAsync(null);
        Message = row.Error;
        RefreshModelReady();
    }

    /// <summary>
    /// Nothing awaits this, so nothing would see it fail.
    /// </summary>
    /// <remarks>
    /// Enumerating devices swallows its own errors today, so this catches nothing yet — which is the
    /// point: an unhandled fault on a task nobody awaits reaches <c>CrashHandler</c> as an unobserved
    /// exception instead of the one place the user is looking. The settings tab starts the same call the
    /// same way and guards it; two spellings of one pattern is how the unguarded one survives.
    /// </remarks>
    private async Task SafeLoadDevicesAsync()
    {
        try
        {
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            Message = $"The microphones could not be listed: {ex.Message}";
        }
    }

    private async Task LoadDevicesAsync()
    {
        // Off the thread, like everywhere else this is asked: the first call is native portaudio
        // initialising, which is most of a second.
        // Rescanning, unlike the tab's own fill: this step is "choose your microphone", and plugging one
        // in is exactly what somebody does while they are on it. The backend only enumerates when it is
        // initialised, so without this a headset connected a minute ago is not on the list.
        var names = await Task.Run(() => _dictation.GetInputDevices(rescan: true)).ConfigureAwait(true);
        var configured = _settings.Settings.Speech.InputDeviceName;

        Devices.Clear();
        Devices.Add(SettingsViewModel.DefaultDeviceOption);
        foreach (var name in names)
            Devices.Add(name);

        // A device that is not plugged in right now stays in the list and stays chosen.
        if (!string.IsNullOrEmpty(configured) && !Devices.Contains(configured))
            Devices.Add(configured);

        Device = string.IsNullOrEmpty(configured) ? SettingsViewModel.DefaultDeviceOption : configured;

        if (names.Count == 0)
            Message = "No microphone was found. Dictation needs one; the rest of this is still worth setting up.";
    }

    /// <summary>Records, or ends the recording this wizard started.</summary>
    [RelayCommand]
    private void ToggleTest()
    {
        // Only a *recording* is ended by this button. The same shape with `!= Idle` — which is what this
        // was, copied from the tile before that one was fixed — sends a click during transcription into
        // Stop, which returns early when nothing is recording: the button answers with nothing at all.
        // Falling through to Start gets it refused with a reason instead.
        if (_dictation.State == DictationState.Recording && ReferenceEquals(_dictation.Owner, this))
        {
            _dictation.Stop();
            return;
        }

        Message = null;
        Transcript = "";

        // Straight into this window rather than through DictationTextSink: the point of the step is to
        // show the words, not to type them somewhere.
        _dictation.Start(this, text =>
        {
            Transcript = text;
            return true;
        });
    }

    private void OnDictationChanged()
    {
        var mine = ReferenceEquals(_dictation.Owner, this);
        IsRecording = mine && _dictation.State == DictationState.Recording;
        IsTranscribing = mine && _dictation.State == DictationState.Transcribing;
    }

    [RelayCommand]
    private void Back()
    {
        if (SpeechSetupFlow.Previous(Step) is { } previous)
            Step = previous;
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
            return;

        if (SpeechSetupFlow.Next(Step) is { } next)
            Step = next;
        else
            CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>
    /// Takes the microphone back if the wizard is closed mid-sentence, and lets go of the service.
    /// </summary>
    /// <remarks>
    /// <para>The service outlives this window by a long way. A recording left running would have nowhere
    /// to deliver to and would hold the microphone until its five-minute cap; a handler left subscribed
    /// would keep the whole wizard alive behind it.</para>
    /// <para>A download in progress is stopped for the same reason and not out of thrift: the only
    /// reference to its cancellation is the row that just left the screen, so half a gigabyte would go on
    /// arriving with no progress shown and no way to stop it. Nothing is thrown away — the bytes stay in
    /// the <c>.partial</c> file and the next attempt resumes from them, which is what that file is
    /// for.</para>
    /// <para>Once, like every other <c>Dispose</c> here — and stated rather than relied upon. A second
    /// call happens to be harmless today: the unsubscribes are, and the restore below re-reads the
    /// setting it just wrote and finds nothing to do. That is an accident of the current steps, not a
    /// property of the method, and the steps are exactly the kind that grow. The window's own teardown
    /// and a caller tidying up can both reach this.</para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _dictation.StateChanged -= OnDictationChanged;
        _dictation.Error -= OnDictationError;

        if (ReferenceEquals(_dictation.Owner, this))
            _dictation.Cancel();

        foreach (var model in Models)
            model.CancelDownloadCommand.Execute(null);

        // Half-finished is not a configuration. If what is chosen now is not on the machine and
        // something that was is, that is what dictation goes back to.
        RefreshModelReady();
        if (!IsModelReady && _workingAtOpen is { } working
            && !string.Equals(working, _settings.Settings.Speech.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Settings.Speech.ModelId = working;
            _settings.NotifyChanged();
        }
    }
}
