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
    /// Which keystrokes are an answer — and which must be left alone, unhandled — is
    /// <see cref="Services.Speech.HotkeyCapture"/>, shared with the setup wizard's own shortcut field and
    /// testable without a window. All that is left here is applying the answer.
    /// </remarks>
    private void SpeechHotkey_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var capture = Services.Speech.HotkeyCapture.Interpret(e.Key, e.KeyModifiers);
        if (!capture.Taken)
            return;

        vm.SpeechHotkey = capture.Action == Services.Speech.HotkeyCaptureAction.Clear
            ? ""
            : capture.Gesture.ToString();
        e.Handled = true;
    }
}
