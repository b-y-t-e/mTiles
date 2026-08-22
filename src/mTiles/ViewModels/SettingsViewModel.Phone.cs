using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>
/// The phone-dictation settings, on the Speech tab.
/// </summary>
/// <remarks>
/// A section on that tab rather than a tab of its own: it is the same feature reached from a different
/// microphone, and a user looking for it will look under dictation. Saved as you type, like the rest of
/// the tab — but unlike the rest of the tab, two of these do restart a running service, which is why the
/// bridge debounces what it hears from here rather than acting on every intermediate value.
/// </remarks>
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _phoneEnabled;
    // No initialiser: InitializePhone writes the stored value into the field before anything binds, so
    // one here is a second opinion about the default that only ever disagrees with PhoneSettings.
    [ObservableProperty] private int _phonePort;
    [ObservableProperty] private bool _phoneAutoSubmit;

    private void InitializePhone()
    {
        var phone = _settingsService.Settings.Phone;

        // Fields, for the reason InitializeSpeech gives: this runs from the constructor, and the setters
        // save.
#pragma warning disable MVVMTK0034
        _phoneEnabled = phone.Enabled;
        _phonePort = phone.Port;
        _phoneAutoSubmit = phone.AutoSubmitEnter;
#pragma warning restore MVVMTK0034
    }

    partial void OnPhoneEnabledChanged(bool value) => SavePhone(p => p.Enabled = value);

    partial void OnPhoneAutoSubmitChanged(bool value) => SavePhone(p => p.AutoSubmitEnter = value);

    /// <summary>
    /// Stores a port, refusing values that cannot be listened on.
    /// </summary>
    /// <remarks>
    /// <para>Anything the box can produce is stored, including a privileged port the application cannot
    /// bind. That is not laziness: rejecting it here rejected it <em>silently</em>, leaving the number on
    /// screen disagreeing with the number in the file, with nothing to say which one was real. The bridge
    /// already falls back to a free port when it cannot bind the one it was given, and says so in the
    /// panel — so an unusable value now produces a visible explanation instead of a silent no-op.</para>
    /// <para>Zero is a real answer: "pick one for me".</para>
    /// </remarks>
    partial void OnPhonePortChanged(int value) => SavePhone(p => p.Port = value);

    private void SavePhone(Action<PhoneSettings> change)
    {
        change(_settingsService.Settings.Phone);
        _settingsService.NotifyChanged();
    }
}
