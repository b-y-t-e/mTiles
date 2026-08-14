using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MsgBox = MsBox.Avalonia.MessageBoxManager;
using MsBox.Avalonia.Enums;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            // No window means no question, and an unanswered question is not a yes. Every caller of
            // this confirms something destructive — deleting a connection, discarding a downloaded
            // model — so the safe answer when it cannot be asked is no.
            vm.ConfirmAction = async message =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return false;
                var box = MsgBox.GetMessageBoxStandard("Confirm", message, ButtonEnum.YesNo, Icon.Question);
                var result = await box.ShowWindowDialogAsync(window);
                return result == ButtonResult.Yes;
            };
            vm.RunSpeechSetup = async () =>
            {
                if (TopLevel.GetTopLevel(this) is not Window window || vm.Dictation is not { } dictation)
                    return;

                await SpeechSetupWizard.ShowAsync(window, dictation, vm.SettingsService);
            };
            vm.ShowError = async (title, message) =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return;
                var box = MsgBox.GetMessageBoxStandard(title, message, ButtonEnum.Ok, Icon.Warning);
                await box.ShowWindowDialogAsync(window);
            };
            vm.BrowseAiToolFile = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Select AI tool executable",
                        AllowMultiple = false
                    });

                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };

            vm.BrowseGitFile = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Select git executable",
                        AllowMultiple = false
                    });

                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };
        }
    }

    /// <summary>
    /// Held while the shortcut box has the keyboard, so the dictation shortcut stands down.
    /// </summary>
    /// <remarks>
    /// Otherwise the feature cannot be reconfigured: the shortcut handler tunnels from the window, so it
    /// would see Alt+Space first, start recording, and swallow the keystroke this box was waiting for —
    /// leaving the transcript in a terminal behind the settings dialog. One object, released by
    /// whichever of the three exits happens first: losing focus, the dialog being hidden, the view
    /// leaving the tree.
    /// </remarks>
    private IDisposable? _rebinding;

    private void SpeechHotkey_GotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _rebinding = Services.Speech.DictationHotkeys.BeginRebinding();

    private void SpeechHotkey_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => EndRebinding();

    private void EndRebinding()
    {
        _rebinding?.Dispose();
        _rebinding = null;
    }

    /// <summary>
    /// The settings dialog going away puts the flag down as well.
    /// </summary>
    /// <remarks>
    /// Closing it while the shortcut box still has the keyboard — Escape, the close button, a click
    /// outside — need not raise LostFocus, and a flag left up is a shortcut that never records again.
    /// The handler's own focus check covers most of it, but only for as long as the focus manager stops
    /// naming a box that is no longer on screen; this is the half that does not depend on that. By
    /// visibility rather than by detaching, because the dialog is an overlay that is hidden, not removed.
    /// </remarks>
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && !IsVisible)
            EndRebinding();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndRebinding();
    }

    /// <summary>
    /// Records the shortcut the user presses in the box, rather than asking them to spell it.
    /// </summary>
    /// <remarks>
    /// <para>Three keys are let through untouched rather than recorded. A bare modifier is what every
    /// combination starts with, so acting on it would store "Alt" the moment somebody reached for
    /// Alt+Space. <b>Tab</b> is how you leave the box — swallowing it traps the keyboard here. And
    /// <b>Escape</b> is how you leave the dialog; recorded, it would bind the key that cancels dictation
    /// to starting it, and swallowed, it would strand the user in a settings dialog that will not close.</para>
    /// <para>Which is why the event is marked handled only where the key is actually taken: doing it
    /// first, before the early exits, is the whole of that bug.</para>
    /// </remarks>
    private void SpeechHotkey_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        if (Services.Speech.HotkeyGesture.IsModifierKey(e.Key)
            || e.Key is Avalonia.Input.Key.Tab or Avalonia.Input.Key.Escape)
            return;

        // Backspace and Delete clear it — the convention every shortcut field follows, and the only way
        // to say "no shortcut" with the keyboard now that there is no separate switch. Bound as a
        // gesture they would be useless anyway: a bare Backspace would eat the key everywhere.
        if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete
            && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            vm.SpeechHotkey = "";
            e.Handled = true;
            return;
        }

        vm.SpeechHotkey = new Services.Speech.HotkeyGesture(e.KeyModifiers, e.Key).ToString();
        e.Handled = true;
    }
}
