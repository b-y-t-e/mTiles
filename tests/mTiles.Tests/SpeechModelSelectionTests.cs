using mTiles.Services.Speech;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which model the Speech tab settles on when the set of models on disk changes underneath it.
/// </summary>
/// <remarks>
/// The rule has one shape and two directions, and it was got wrong in both, a week apart: downloading a
/// model that is not the default left the feature insisting no model existed, and deleting the model in
/// use left it insisting the same while another sat on disk. Both look to a user like dictation simply
/// not working, and neither shows up anywhere except by trying it.
/// </remarks>
public class SpeechModelSelectionTests : IDisposable
{
    private sealed class SilentCapture : IAudioCapture
    {
        public bool IsAvailable => true;
        public bool IsRecording => false;
        public IReadOnlyList<string> Devices { get; set; } = [];
        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => Devices;
        public void Start(string deviceName) { }
        public IRecordingHandle? Detach() => null;
        public float[] Finish(IRecordingHandle? detached) => [];
        public void Dispose() { }
    }

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public SpeechModelSelectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Puts a model on "disk" — a file of exactly the right size, which is what IsDownloaded
    /// asks for.</summary>
    private void PlaceOnDisk(string modelId)
    {
        var model = SpeechModelCatalog.Find(modelId)!;
        using var file = File.Create(Path.Combine(_directory, model.FileName));
        file.SetLength(model.DownloadBytes);
    }

    private (SettingsViewModel Tab, TempSettings Settings) Build(SilentCapture? capture = null)
    {
        var settings = new TempSettings();
        var store = new SpeechModelStore(_directory);
        var dictation = new DictationService(settings.Service, capture ?? new SilentCapture(), store: store,
            dispatch: action => action());

        var tab = new SettingsViewModel(settings.Service, dictation: dictation) { SelectedTab = 4 };
        return (tab, settings);
    }

    /// <summary>
    /// The tab, with the device scan it starts on opening already finished.
    /// </summary>
    /// <remarks>
    /// Opening the tab kicks off a scan in the background. In the application its tail runs on the UI
    /// thread and is uninterruptible; here the test would be the second thread, changing the setting
    /// half-way through it and measuring a race no user can reach. Refreshes are serialised, so awaiting
    /// one waits for that one too — this is settling the fixture, not papering over the behaviour.
    /// </remarks>
    private static async Task SettleAsync(SettingsViewModel tab) =>
        await tab.RefreshSpeechDevicesCommand.ExecuteAsync(null);

    /// <summary>
    /// Rescanning microphones does not lose the one that is chosen.
    /// </summary>
    /// <remarks>
    /// Two bugs met here and both were silent. Emptying the bound collection makes the combo box push a
    /// null selection through, which is a write to settings — so the device was erased before the code
    /// that meant to preserve it ever read it. The fix is on both sides: the setting is read before the
    /// list is cleared, <em>and</em> an empty selection is refused. This drives the real command.
    /// </remarks>
    [Fact]
    public async Task Rescanning_devices_keeps_the_chosen_microphone()
    {
        var capture = new SilentCapture { Devices = ["Yeti", "Webcam"] };
        var (tab, settings) = Build(capture);
        using var _ = settings;
        await SettleAsync(tab);

        tab.SpeechDevice = "Yeti";
        Assert.Equal("Yeti", settings.Service.Settings.Speech.InputDeviceName);

        await tab.RefreshSpeechDevicesCommand.ExecuteAsync(null);

        Assert.Equal("Yeti", settings.Service.Settings.Speech.InputDeviceName);
        Assert.Equal("Yeti", tab.SpeechDevice);
        Assert.Contains("Yeti", tab.SpeechDevices);
        Assert.Equal(SettingsViewModel.DefaultDeviceOption, tab.SpeechDevices[0]);
    }

    /// <summary>
    /// A device that is not there right now stays chosen, and stays in the list.
    /// </summary>
    /// <remarks>
    /// A headset unplugged for an hour, or one enumeration hiccup, must not rewrite the setting —
    /// recording falls back to the system default on its own when the device really is missing.
    /// </remarks>
    [Fact]
    public async Task An_unplugged_microphone_is_still_the_chosen_one()
    {
        var capture = new SilentCapture { Devices = ["Yeti"] };
        var (tab, settings) = Build(capture);
        using var _ = settings;
        await SettleAsync(tab);

        tab.SpeechDevice = "Yeti";
        capture.Devices = [];                          // unplugged between two scans

        await tab.RefreshSpeechDevicesCommand.ExecuteAsync(null);

        Assert.Equal("Yeti", settings.Service.Settings.Speech.InputDeviceName);
        Assert.Contains("Yeti", tab.SpeechDevices);
    }

    /// <summary>The system default is stored as an empty name, so it survives a rescan too.</summary>
    [Fact]
    public async Task The_system_default_survives_a_rescan()
    {
        var capture = new SilentCapture { Devices = ["Yeti"] };
        var (tab, settings) = Build(capture);
        using var _ = settings;
        await SettleAsync(tab);

        Assert.Equal("", settings.Service.Settings.Speech.InputDeviceName);

        await tab.RefreshSpeechDevicesCommand.ExecuteAsync(null);

        Assert.Equal("", settings.Service.Settings.Speech.InputDeviceName);
        Assert.Equal(SettingsViewModel.DefaultDeviceOption, tab.SpeechDevice);
    }

    [Fact]
    public void A_model_that_appears_is_adopted_when_the_chosen_one_is_not_on_disk()
    {
        PlaceOnDisk("base");
        var (tab, settings) = Build();
        using var _ = settings;

        var downloaded = tab.SpeechModels.First(m => m.Id == "base");
        Assert.True(downloaded.IsDownloaded);
        Assert.DoesNotContain(tab.SpeechModels, m => m.IsInUse);

        tab.AdoptIfNothingUsable(downloaded);

        Assert.Equal("base", settings.Service.Settings.Speech.ModelId);
        Assert.True(downloaded.IsInUse);
    }

    /// <summary>Deleting the model in use falls back to any other one on disk — the same rule, entered
    /// from the other side, which is where it was missing.</summary>
    [Fact]
    public void When_the_model_in_use_disappears_another_one_on_disk_takes_over()
    {
        PlaceOnDisk("base");
        PlaceOnDisk("small");
        var (tab, settings) = Build();
        using var _ = settings;

        settings.Service.Settings.Speech.ModelId = "small";
        var gone = tab.SpeechModels.First(m => m.Id == "small");
        var kept = tab.SpeechModels.First(m => m.Id == "base");
        gone.IsSelected = true;
        kept.IsSelected = false;

        // What DeleteAsync leaves behind: selected, no longer on disk, nothing else selected.
        gone.IsDownloaded = false;
        tab.AdoptIfNothingUsable(null);

        Assert.Equal("base", settings.Service.Settings.Speech.ModelId);
        Assert.True(kept.IsInUse);
    }

    /// <summary>
    /// Deleting without a confirmation dialog wired removes nothing, and says why.
    /// </summary>
    /// <remarks>
    /// The convention elsewhere in this application is that an unwired ConfirmAction lets the action
    /// through, which is the wrong way round for a click that discards hundreds of megabytes and hours
    /// of somebody's connection. Refusing silently would be its own bug, hence the message.
    /// </remarks>
    [Fact]
    public async Task Deleting_without_a_confirmation_wired_does_nothing_and_says_so()
    {
        PlaceOnDisk("base");
        var (tab, settings) = Build();          // through the tab, which is what wires the rows
        using var _ = settings;

        Assert.Null(tab.ConfirmAction);         // no view attached: nothing to ask with
        var row = tab.SpeechModels.First(m => m.Id == "base");

        await row.DeleteCommand.ExecuteAsync(null);

        Assert.True(row.IsDownloaded);
        Assert.True(File.Exists(Path.Combine(_directory, SpeechModelCatalog.Find("base")!.FileName)));
        Assert.NotNull(row.Error);
    }

    /// <summary>
    /// After the setup wizard, the tab shows what the wizard did — not what it held before.
    /// </summary>
    /// <remarks>
    /// <para>The wizard writes straight to settings, so every copy this tab holds is stale the moment it
    /// closes: the model row, the language, the shortcut, the microphone. They are copies on purpose —
    /// <c>InitializeSpeech</c> writes the backing fields, because going through the setters would save
    /// every value back to disk on startup — which is exactly why nothing here notices a change until it
    /// is told about one.</para>
    /// <para>The device <em>list</em> as well as the chosen device: the wizard enumerates microphones
    /// itself, so somebody who plugs a headset in while it is open ends up with a device this tab has
    /// never heard of, and a combo box whose value is not among its items shows nothing at all.</para>
    /// </remarks>
    [Fact]
    public async Task Running_the_wizard_leaves_the_tab_showing_what_it_changed()
    {
        PlaceOnDisk("base");
        var capture = new SilentCapture { Devices = ["Webcam"] };
        var (tab, settings) = Build(capture);
        using var _ = settings;
        await SettleAsync(tab);

        Assert.DoesNotContain("Yeti", tab.SpeechDevices);

        // What the wizard does, without a window: choose a model, a microphone that appeared while it
        // was open, and a language.
        tab.RunSpeechSetup = () =>
        {
            capture.Devices = ["Webcam", "Yeti"];
            var speech = settings.Service.Settings.Speech;
            speech.ModelId = "base";
            speech.InputDeviceName = "Yeti";
            speech.Language = "pl";
            return Task.CompletedTask;
        };

        await tab.SetUpSpeechCommand.ExecuteAsync(null);

        Assert.Equal("pl", tab.SpeechLanguage);
        Assert.Equal("Yeti", tab.SpeechDevice);
        Assert.Contains("Yeti", tab.SpeechDevices);
        Assert.True(tab.SpeechModels.First(m => m.Id == "base").IsSelected);
    }

    /// <summary>A working choice is never overridden — that would be the feature quietly changing a
    /// model the user picked on purpose.</summary>
    [Fact]
    public void A_chosen_model_that_is_present_is_left_alone()
    {
        PlaceOnDisk("base");
        PlaceOnDisk("small");
        var (tab, settings) = Build();
        using var _ = settings;

        settings.Service.Settings.Speech.ModelId = "small";
        tab.SpeechModels.First(m => m.Id == "small").IsSelected = true;

        tab.AdoptIfNothingUsable(tab.SpeechModels.First(m => m.Id == "base"));

        Assert.Equal("small", settings.Service.Settings.Speech.ModelId);
    }
}
