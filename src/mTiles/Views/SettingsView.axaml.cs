using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class SettingsView : UserControl,
    OverlayHost.IOverlayHeader, OverlayHost.IConfirmsClose
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

    }

    /// <summary>The four tabs, drawn beside the dialog's close button.</summary>
    /// <remarks>They used to be part of a card hand-written in <c>MainWindow</c>, which is most of why
    /// Settings was a dialog of its own kind: nothing else needed anything in that row. Declared in
    /// this view's markup now, where the thing they switch between also lives.
    /// <para>A fresh control each time, and a control of its own rather than a piece of this page: a
    /// control has one parent, so handing the host something already inside the page asks Avalonia to
    /// draw the same StackPanel in two places. It throws, and the dialog closes the instant it opens —
    /// which is exactly what it did.</para></remarks>
    public Control? OverlayHeader => new SettingsTabsHeader { DataContext = DataContext };

    /// <summary>The unsaved-changes question, asked before the X or Escape takes the dialog down.</summary>
    /// <remarks>Applying the database form restarts the service, so it is the one page here that does
    /// not persist as you type, and leaving with changes pending is a question rather than an action.
    /// The view model owns the question; the host only has to know it may be refused.</remarks>
    public async Task<bool> CanCloseAsync() =>
        DataContext is not SettingsViewModel vm || await vm.TryCloseAsync();

    private SettingsViewModel? _subscribed;

    /// <summary>What an exported settings file is, for both pickers — one definition, so the dialog
    /// that writes it and the one that reads it back cannot disagree about the extension.</summary>
    private static readonly FilePickerFileType SettingsFileType =
        new("Settings") { Patterns = ["*.json"] };

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
        }

        if (DataContext is SettingsViewModel vm)
        {
            _subscribed = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            // No window means no question, and an unanswered question is not a yes. Every caller of
            // this confirms something destructive — deleting a connection, discarding a downloaded
            // model — so the safe answer when it cannot be asked is no.
            vm.ConfirmAction = message =>
                MessageDialog.ConfirmAsync(this, "Confirm", message, whenUnavailable: false);
            vm.RunSpeechSetup = async () =>
            {
                if (vm.Dictation is not { } dictation) return;
                await SpeechSetupWizard.ShowAsync(this, dictation, vm.SettingsService);
            };
            vm.ShowError = (title, message) =>
                MessageDialog.ShowAsync(this, title, message, MessageDialog.Tone.Warning);
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
            vm.BrowseSaveFile = async suggested =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Export settings",
                        SuggestedFileName = suggested,
                        DefaultExtension = "json",
                        FileTypeChoices = [SettingsFileType],
                    });

                return file?.TryGetLocalPath();
            };
            vm.BrowseOpenFile = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Import settings",
                        AllowMultiple = false,
                        FileTypeFilter = [SettingsFileType],
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


    /// <summary>The form this page has open, if any.</summary>
    private SettingsEditDialog? _editing;

    /// <summary>
    /// Makes the edit form modal to the keyboard, the way every other dialog in the application is.
    /// </summary>
    /// <remarks>This overlay is opened through <c>OverlayHost</c> like every other dialog in the
    /// application, but bound to a view model flag rather than awaited — the form is toggled open and
    /// closed by <see cref="SettingsViewModel.IsEditingAnything"/> rather than by a single
    /// <c>ShowAsync</c> call, since the caller here has no result to wait for. Going through the host is
    /// what gives it the same keyboard modality as every other dialog: without it, Tab walked out of a
    /// half-filled provider row into the settings page behind it, and Alt+Space dictated into the
    /// terminal tile behind that.</remarks>
    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.IsEditingAnything)) return;
        if (sender is not SettingsViewModel vm) return;

        if (!vm.IsEditingAnything)
        {
            if (_editing is { } open)
            {
                _editing = null;
                OverlayHost.CloseWith(open, null);
            }
            return;
        }

        if (_editing is not null || OverlayHost.For(this) is not { } host)
            return;

        var dialog = new SettingsEditDialog { DataContext = DataContext };
        _editing = dialog;

        // async void: nothing may escape to the dispatcher, where it is a crash rather than a form
        // that failed to open.
        try
        {
            await host.ShowAsync<object>(dialog, width: 560);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"The settings form failed: {ex.Message}");
        }
        finally
        {
            // Closed by the host's own X or Escape rather than by Save or Cancel, so the view model
            // still believes it is editing — and would refuse to open the next form.
            if (ReferenceEquals(_editing, dialog))
            {
                _editing = null;
                (DataContext as SettingsViewModel)?.CancelEditing();
            }
        }
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
