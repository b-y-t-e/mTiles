using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Speech;

namespace mTiles.ViewModels;

/// <summary>
/// The Speech tab. Saved as you type, like every tab but Database — nothing here restarts a service.
/// </summary>
public partial class SettingsViewModel
{
    private DictationService? _dictation;
    private SpeechModelStore? _modelStore;

    /// <summary>What the view needs to open the wizard: the service it drives and the settings it writes.
    /// Exposed rather than duplicated — the wizard writes to the same file this tab does.</summary>
    internal DictationService? Dictation => _dictation;
    internal SettingsService SettingsService => _settingsService;

    public ObservableCollection<SpeechModelViewModel> SpeechModels { get; } = [];
    public ObservableCollection<string> SpeechDevices { get; } = [];
    public static IReadOnlyList<ComboOption> SpeechLanguages { get; } =
        [.. SpeechModelCatalog.Languages.Select(l => new ComboOption(l.Code, l.Name, true))];

    [ObservableProperty] private bool _speechEnabled;
    [ObservableProperty] private string _speechLanguage = "auto";
    [ObservableProperty] private string _speechDevice = "";
    [ObservableProperty] private string _speechHotkey = "";
    [ObservableProperty] private bool _speechPushToTalk = true;
    [ObservableProperty] private bool _speechAutoSubmit;
    [ObservableProperty] private bool _speechAppendTrailingSpace = true;
    [ObservableProperty] private bool _speechRemoveFillerWords = true;
    [ObservableProperty] private bool _speechTranslateToEnglish;
    [ObservableProperty] private string _speechCustomWords = "";
    /// <summary>Overwritten from settings in <see cref="InitializeSpeech"/>; the initialiser only has to
    /// agree with <c>SpeechSettings.ModelUnloadMinutes</c> so the two cannot tell different stories.</summary>
    [ObservableProperty] private int _speechUnloadMinutes = 30;
    [ObservableProperty] private string? _speechHotkeyWarning;

    /// <summary>Empty until the tab is opened, because enumerating devices loads the audio backend.</summary>
    private bool _speechLoaded;

    /// <summary>
    /// False when there is no audio backend at all — the tab then explains itself.
    /// </summary>
    /// <remarks>
    /// A stored answer, not a question asked on every render. Asking means
    /// <c>PortAudioRuntime.EnsureInitialized</c>, which loads the native library and initialises it, and
    /// a bound property is read on the UI thread the moment the tab draws — which walked straight past
    /// the background thread <see cref="RefreshSpeechDevicesAsync"/> exists to put that work on.
    /// </remarks>
    [ObservableProperty]
    private bool _isSpeechAvailable = true;

    /// <summary>The default device, shown as the first entry so "" has a name.</summary>
    public static string DefaultDeviceOption => "(system default)";

    private void InitializeSpeech(DictationService? dictation)
    {
        _dictation = dictation;

        // Kept once. This runs again every time the setup wizard closes, and a store is not a bag of
        // static helpers any more: it holds the gate that stops two lists downloading — or downloading
        // and deleting — the same file at once. A fresh one each time is a fresh gate, which is no gate.
        _modelStore ??= dictation?.Store ?? new SpeechModelStore();

        var speech = _settingsService.Settings.Speech;

        // The fields, not the properties: this runs from the constructor, before anything is bound, and
        // going through the setters would write every stored value straight back to disk on startup.
#pragma warning disable MVVMTK0034
        _speechEnabled = speech.Enabled;
        _speechLanguage = speech.Language;
        _speechDevice = string.IsNullOrEmpty(speech.InputDeviceName) ? DefaultDeviceOption : speech.InputDeviceName;
        _speechHotkey = speech.Hotkey;
        _speechPushToTalk = speech.Mode == DictationMode.PushToTalk;
        _speechAutoSubmit = speech.AutoSubmitEnter;
        _speechAppendTrailingSpace = speech.AppendTrailingSpace;
        _speechRemoveFillerWords = speech.RemoveFillerWords;
        _speechTranslateToEnglish = speech.TranslateToEnglish;
        _speechCustomWords = string.Join(", ", speech.CustomWords);
        _speechUnloadMinutes = speech.ModelUnloadMinutes;

        // Worked out from the shortcut rather than left alone. This method exists to load values without
        // saving them back, so it writes fields — and a field that nothing recomputes keeps the warning
        // that belonged to the previous shortcut. It runs at startup, where the shortcut comes from a
        // file nobody has validated, and again when the setup wizard closes, where it comes from another
        // window entirely.
        _speechHotkeyWarning = HotkeyAdvice.ForSetting(speech.Hotkey);
#pragma warning restore MVVMTK0034
    }

    /// <summary>
    /// Fills the model and device lists. Called every time the tab is opened.
    /// </summary>
    /// <remarks>
    /// Built once, but each row is asked again whether its model is on disk. A model directory is a
    /// place on the user's filesystem: it can be emptied between two visits to this tab — by hand, by a
    /// disk cleaner, by a download this application did not make — and a row insisting a deleted model
    /// is still there offers no way to get it back.
    /// </remarks>
    private void LoadSpeechOptions()
    {
        if (_modelStore is null)
            return;

        if (_speechLoaded)
        {
            foreach (var model in SpeechModels)
                model.RefreshDownloaded();
            return;
        }

        _speechLoaded = true;
        var selectedId = _settingsService.Settings.Speech.ModelId;
        if (string.IsNullOrEmpty(selectedId))
            selectedId = SpeechModelCatalog.DefaultModelId;

        SpeechModels.Clear();
        foreach (var model in SpeechModelCatalog.All)
        {
            var vm = new SpeechModelViewModel(model, _modelStore, model.Id == selectedId)
            {
                SelectRequested = SelectSpeechModel,
                // A loaded model has its files open, and this is the only place that knows both which
                // model a row is and where the engine holding it lives. Named, so deleting one model
                // does not evict a different one that somebody is about to dictate with.
                ReleaseModel = () => _dictation?.UnloadModel(_modelStore.GetPath(model)),
                AvailabilityChanged = AdoptIfNothingUsable,
            };

            // Not `?? Task.FromResult(true)`. Wrapping a missing dialog in a yes made the row's own
            // refusal unreachable and deleted hundreds of megabytes without asking anyone; the row is
            // told no, and told why, so the button explains itself instead of appearing broken.
            vm.ConfirmAction = message =>
            {
                if (ConfirmAction is { } confirm)
                    return confirm(message);

                vm.Error = SpeechModelViewModel.NothingToConfirmWith;
                return Task.FromResult(false);
            };

            SpeechModels.Add(vm);
        }

        // Not awaited, like the AI tools tab: the model list is on screen immediately and the devices —
        // and with them the answer to whether audio works at all — fill in behind it. Guarded, because
        // an unhandled fault on a task nobody awaits is an unobserved exception rather than a message.
        _ = SafeRefreshSpeechDevicesAsync();
    }

    private async Task SafeRefreshSpeechDevicesAsync()
    {
        try
        {
            await ReloadSpeechDevicesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Listing microphones failed: {0}", ex);
            IsSpeechAvailable = false;
        }
    }

    /// <summary>
    /// Selects <paramref name="modelId"/> and starts fetching it — what the first-run question does with
    /// the answer.
    /// </summary>
    /// <remarks>
    /// Through this tab rather than through the store directly, so the download the user just asked for
    /// is the one on screen, with the progress and the Cancel button they already have. Building a
    /// second download UI for the same operation is how the two drift.
    /// </remarks>
    public void StartSpeechDownload(string modelId)
    {
        LoadSpeechOptions();

        if (SpeechModels.FirstOrDefault(m => m.Id == modelId) is not { } row)
            return;

        SelectSpeechModel(row);
        if (!row.IsDownloaded)
            row.DownloadCommand.Execute(null);
    }

    /// <summary>The refresh currently running, so a second one queues behind it rather than
    /// interleaving.</summary>
    private Task _deviceRefresh = Task.CompletedTask;

    /// <summary>
    /// The Rescan button. Asks the backend to look at the hardware again, which is the only way a device
    /// plugged in since the application started can appear at all.
    /// </summary>
    /// <remarks>
    /// <para>Off the UI thread: enumerating devices is measured in tens of milliseconds <em>per device</em>
    /// — Handy documents 40–85 ms for the same queries — and a rescan additionally tears the audio
    /// library down and brings it back.</para>
    /// <para>Serialised with every other refresh, because two of these genuinely overlap: the tab starts
    /// one the moment it is opened and this button can start another while the first is still inside its
    /// <c>await</c>. Interleaved, they clear and refill the same bound collection from two continuations
    /// and can settle the selection from a snapshot the other has already overtaken; the observed result
    /// was the chosen microphone quietly falling back to the system default.</para>
    /// </remarks>
    [RelayCommand]
    private Task RefreshSpeechDevicesAsync() => QueueDeviceRefresh(rescan: true);

    /// <summary>The same list, from what the backend already knows — for filling the tab in, where
    /// nothing has changed and tearing the audio library down to find that out would be absurd.</summary>
    private Task ReloadSpeechDevicesAsync() => QueueDeviceRefresh(rescan: false);

    private Task QueueDeviceRefresh(bool rescan)
    {
        _deviceRefresh = RunAfterAsync(_deviceRefresh);
        return _deviceRefresh;

        async Task RunAfterAsync(Task previous)
        {
            // Whatever the one before did, including failing: this one still has to run.
            try { await previous.ConfigureAwait(true); }
            catch { /* its own caller heard about it */ }

            await RefreshDevicesCoreAsync(rescan).ConfigureAwait(true);
        }
    }

    private async Task RefreshDevicesCoreAsync(bool rescan)
    {
        // Through the service, which owns the microphone: opening a second audio backend from a view
        // model would also mean a test of this tab touching real hardware. The availability answer comes
        // back from the same trip, because finding it out is the same expensive initialisation.
        var (names, available) = await Task.Run(() =>
            (Names: _dictation?.GetInputDevices(rescan) ?? [], Available: _dictation?.IsAudioAvailable ?? false))
            .ConfigureAwait(true);

        // Read *before* the list is emptied. Clearing the bound collection makes the combo box push a
        // null selection through SpeechDevice, which saves — so by the time the old code looked the
        // setting up, it had already overwritten it with nothing. Rescanning devices erased the chosen
        // microphone, quietly and for good. (OnSpeechDeviceChanged now refuses the null as well; either
        // fix alone would do, and the pair is what makes the order here stop mattering.)
        var configured = _settingsService.Settings.Speech.InputDeviceName;

        IsSpeechAvailable = available;
        SpeechDevices.Clear();
        SpeechDevices.Add(DefaultDeviceOption);
        foreach (var name in names)
            SpeechDevices.Add(name);

        // A device that is not plugged in right now still stays in the list, and stays selected.
        // Selecting something else here writes it straight back to settings, so a headset that happened
        // to be unplugged — or one enumeration hiccup — used to erase the user's choice for good.
        // Recording falls back to the system default on its own when the device really is missing.
        if (!string.IsNullOrEmpty(configured) && !SpeechDevices.Contains(configured))
            SpeechDevices.Add(configured);

        // Asked again, right at the point of use, rather than reusing the value read above. This method
        // spans an await — the first one initialises native portaudio and takes the better part of a
        // second — and a choice made while it was in flight has to win over a snapshot taken before it.
        // Otherwise a refresh that started earlier lands later and quietly resets the microphone to the
        // system default.
        var current = _settingsService.Settings.Speech.InputDeviceName;
        SpeechDevice = string.IsNullOrEmpty(current) ? DefaultDeviceOption : current;
    }

    [RelayCommand]
    private void OpenSpeechModelsFolder()
    {
        if (_modelStore is null)
            return;

        // Creating it can fail as readily as opening it — a read-only profile, a roaming directory that
        // is not there — and neither is worth taking the application down for.
        try
        {
            System.IO.Directory.CreateDirectory(_modelStore.Directory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Creating the models folder failed: {0}", ex.Message);
            return;
        }

        FileHelper.OpenFolder(_modelStore.Directory);
    }

    /// <summary>
    /// Settles the selection on a model that is actually on disk, whenever one appears or is deleted.
    /// </summary>
    /// <remarks>
    /// Never over a working choice: if the chosen model is present, nothing here touches it. Otherwise
    /// the model just downloaded is adopted — and after a delete, any other model still on disk, since
    /// removing the one in use otherwise leaves dictation reporting a missing model while a perfectly
    /// good one sits beside it.
    /// </remarks>
    internal void AdoptIfNothingUsable(SpeechModelViewModel? preferred)
    {
        if (SpeechModels.Any(model => model.IsInUse))
            return;

        var candidate = preferred is { IsDownloaded: true }
            ? preferred
            : SpeechModels.FirstOrDefault(model => model.IsDownloaded);

        if (candidate is not null)
            SelectSpeechModel(candidate);
    }

    private void SelectSpeechModel(SpeechModelViewModel chosen)
    {
        foreach (var model in SpeechModels)
            model.IsSelected = ReferenceEquals(model, chosen);

        _settingsService.Settings.Speech.ModelId = chosen.Id;
        _settingsService.NotifyChanged();
        OnPropertyChanged(nameof(HasWhisperOnlyOptions));
    }

    /// <summary>Whether the language, translation and vocabulary controls mean anything for the chosen
    /// model. The answer belongs to the model — this only asks it, so a third kind of engine does not
    /// need a second opinion kept in step here.</summary>
    public bool HasWhisperOnlyOptions =>
        (SpeechModelCatalog.Find(_settingsService.Settings.Speech.ModelId)
         ?? SpeechModelCatalog.Find(SpeechModelCatalog.DefaultModelId))?.HasWhisperOnlyOptions == true;

    /// <summary>The language combo binds to the option object; the setting stores the code.</summary>
    public ComboOption? SelectedSpeechLanguage
    {
        get => SpeechLanguages.FirstOrDefault(l => l.Value == SpeechLanguage) ?? SpeechLanguages[0];
        set
        {
            if (value is not null)
                SpeechLanguage = value.Value;
        }
    }

    /// <summary>Minutes of not dictating before the model is dropped from memory. Zero keeps it
    /// loaded — worth offering, because reloading Parakeet costs two seconds and the model costs
    /// hundreds of megabytes of resident memory, and which of those matters is the user's to weigh.</summary>
    partial void OnSpeechUnloadMinutesChanged(int value) => SaveSpeech(s => s.ModelUnloadMinutes = value);

    partial void OnSpeechEnabledChanged(bool value) => SaveSpeech(s => s.Enabled = value);

    partial void OnSpeechLanguageChanged(string value)
    {
        SaveSpeech(s => s.Language = value);
        OnPropertyChanged(nameof(SelectedSpeechLanguage));
    }
    /// <summary>
    /// Opens the setup wizard, wired from the view because only it has a window to be modal over.
    /// </summary>
    /// <remarks>
    /// The same wizard the first run shows. It is not gated on anything — this is the "start over"
    /// button, and somebody clicking it has already decided the current state is not what they want.
    /// The list of models is rebuilt afterwards, because the wizard may have downloaded one.
    /// </remarks>
    public Func<Task>? RunSpeechSetup { get; set; }

    [RelayCommand]
    private async Task SetUpSpeechAsync()
    {
        if (RunSpeechSetup is not { } run)
            return;

        await run();

        foreach (var model in SpeechModels)
            model.RefreshDownloaded();

        // The wizard writes straight to settings, so the tab's own copies are what is now stale.
        // InitializeSpeech deliberately writes the backing *fields* — going through the setters would
        // save every value back — so nothing on this tab knows anything changed until it is told.
        // Told about everything, with a null name: listing the properties by hand meant the list had to
        // be kept in step with a method that assigns eleven of them, and it already was not.
        InitializeSpeech(_dictation);
        SelectSpeechModelFromSettings();
        OnPropertyChanged((string?)null);

        // The microphone too, and not only the name of it: the wizard's own step lists the devices, so
        // somebody who plugged a headset in while it was open has chosen a device this tab has never
        // heard of — and a combo box whose selected value is not among its items shows nothing at all.
        await SafeRefreshSpeechDevicesAsync();
    }

    /// <summary>Re-marks the row the settings name, after something outside this tab changed it.</summary>
    private void SelectSpeechModelFromSettings()
    {
        var id = _settingsService.Settings.Speech.ModelId;
        foreach (var model in SpeechModels)
            model.IsSelected = model.Id == id;
    }

    /// <summary>Turns the shortcut off, which is the same thing as having no shortcut.</summary>
    [RelayCommand]
    private void ClearSpeechHotkey() => SpeechHotkey = "";

    partial void OnSpeechAutoSubmitChanged(bool value) => SaveSpeech(s => s.AutoSubmitEnter = value);
    partial void OnSpeechAppendTrailingSpaceChanged(bool value) => SaveSpeech(s => s.AppendTrailingSpace = value);
    partial void OnSpeechRemoveFillerWordsChanged(bool value) => SaveSpeech(s => s.RemoveFillerWords = value);
    partial void OnSpeechTranslateToEnglishChanged(bool value) => SaveSpeech(s => s.TranslateToEnglish = value);

    partial void OnSpeechPushToTalkChanged(bool value) =>
        SaveSpeech(s => s.Mode = value ? DictationMode.PushToTalk : DictationMode.Toggle);

    /// <summary>
    /// The chosen microphone. An empty selection is <em>not</em> a choice and is never saved: a bound
    /// combo box pushes null through here whenever its items are replaced, so honouring it would mean
    /// that refreshing the device list wipes the setting.
    /// </summary>
    partial void OnSpeechDeviceChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        SaveSpeech(s => s.InputDeviceName = value == DefaultDeviceOption ? "" : value);
    }

    partial void OnSpeechCustomWordsChanged(string value) =>
        SaveSpeech(s => s.CustomWords =
            [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    /// <summary>
    /// A shortcut that cannot be parsed is refused rather than stored: the setting would silently do
    /// nothing, and the user would be left pressing keys at an application that had stopped listening.
    /// </summary>
    partial void OnSpeechHotkeyChanged(string value)
    {
        // Empty is a decision, not a failure to parse one: it is how the shortcut is switched off, so it
        // is saved like any other value. The message is a statement rather than a warning — the key now
        // belongs to whatever is running in the tile.
        if (string.IsNullOrWhiteSpace(value))
        {
            SpeechHotkeyWarning = null;
            SaveSpeech(s => s.Hotkey = "");
            return;
        }

        if (!HotkeyGesture.TryParse(value, out var gesture))
        {
            SpeechHotkeyWarning = HotkeyAdvice.Unparseable;
            return;
        }

        // A shortcut with no modifier takes that key from the terminal — but only while dictation can
        // actually record, which is what the shortcut now checks before claiming anything. The wording
        // is shared with the wizard, which offers the same choice and must not describe it differently.
        SpeechHotkeyWarning = HotkeyAdvice.For(gesture);

        SaveSpeech(s => s.Hotkey = gesture.ToString());
    }

    private void SaveSpeech(Action<SpeechSettings> change)
    {
        change(_settingsService.Settings.Speech);
        _settingsService.NotifyChanged();
    }
}
