using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class MainWindow : Window
{
    private SettingsService? _settingsService;
    private Services.Speech.DictationService? _dictation;
    private Action<string>? _onDictationError;
    private ColumnDefinition? _panelColumn;
    private readonly Dictionary<string, WorkspaceView> _viewCache = new();
    private WorkspaceView? _activeWorkspaceView;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"mTiles {AppInfo.Version}";
        SizeChanged += (_, _) => UpdateSettingsDialogSize();
        TerminalClipboardCoordinator.Attach(this);
    }

    public void BindWindowState(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _panelColumn = MainGrid.ColumnDefinitions[0];
        var s = settingsService.Settings;

        if (s.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (!double.IsNaN(s.WindowWidth) && !double.IsNaN(s.WindowHeight))
            {
                Width = s.WindowWidth;
                Height = s.WindowHeight;
            }

            if (!double.IsNaN(s.WindowX) && !double.IsNaN(s.WindowY))
            {
                Position = new PixelPoint((int)s.WindowX, (int)s.WindowY);
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        _panelColumn.Width = new GridLength(s.WorkspacesPanelWidth, GridUnitType.Pixel);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
                    UpdateSettingsDialogSize();
                else if (e.PropertyName == nameof(MainWindowViewModel.CurrentWorkspace))
                    SwitchWorkspaceView(vm.CurrentWorkspace);
            };
            vm.WorkspaceRemoved += id =>
            {
                if (_viewCache.Remove(id, out var removed))
                    WorkspaceHost.Children.Remove(removed);
            };
            vm.WorkspacesPanel.FocusWorkspaceRequested += () =>
                vm.CurrentWorkspace?.FocusActiveTile();
            if (vm.PhoneBridge is { } phoneBridge)
            {
                vm.ShowPhoneBridge = () => PhoneBridgeDialog.ShowAsync(this, phoneBridge);

                // The same thing DictationHotkeys resolves, so a transcript arriving from a phone lands
                // where one arriving from Alt+Space would: the focused text box first, the tile after.
                phoneBridge.FocusedElement = () => FocusManager?.GetFocusedElement();
            }
            if (vm.Dictation is { } dictation)
            {
                // Window-level and tunnelling, like the clipboard coordinator: terminals consume keys,
                // and a push-to-talk needs the release as well as the press.
                Services.Speech.DictationHotkeys.Attach(this, dictation, settingsService,
                    () => vm.CurrentWorkspace?.ActiveTile,
                    // The settings dialog is an overlay in this window, so it never sees Escape while a
                    // recording is running — and dictating into a settings box is a feature, so the two
                    // are on screen together by design.
                    escapeSpokenFor: () => vm.IsSettingsOpen);
                // Kept so it can be taken back off when the window closes: the service outlives this
                // window, and a handler still on it is a handler that can raise a dialog over nothing.
                _dictation = dictation;
                // Straight through: the service raises Error on the dispatcher already, and posting it
                // again only puts the message one more frame behind the failure it describes.
                _onDictationError = ShowDictationError;
                dictation.Error += _onDictationError;

                _ = OfferSpeechModelAsync(vm, dictation);
            }

            vm.ConfirmAction = async message =>
            {
                var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(
                    "Update Available", message,
                    MsBox.Avalonia.Enums.ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Info);
                var result = await box.ShowWindowDialogAsync(this);
                return result == MsBox.Avalonia.Enums.ButtonResult.Yes;
            };
            SwitchWorkspaceView(vm.CurrentWorkspace);
        }
    }

    /// <summary>
    /// Offers a speech model on a start that has none, once per installation.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately after the window is up rather than during construction: this is a modal over
    /// the application, and a modal in front of a window that has not drawn yet is a dialog floating over
    /// nothing. It also asks the disk (is any model downloaded?), which is not work for the path that
    /// builds the main view.</para>
    /// <para>It is the same wizard Settings → Speech opens, and that is the point: a first run and a
    /// "set it up again" are the same three questions, and two windows asking them would be two windows
    /// to keep in step. The one that used to be here asked which model to download and nothing else,
    /// which could not tell anybody whether dictation actually worked.</para>
    /// </remarks>
    private async Task OfferSpeechModelAsync(MainWindowViewModel vm, Services.Speech.DictationService dictation)
    {
        try
        {
            if (!await WhenOpenedAsync())
                return;

            // Off the UI thread. The question looks cheap and is not: it asks whether an audio backend
            // exists, which is what loads and initialises native portaudio (measured at 394 ms the first
            // time), and then stats every model in the catalogue. On the thread that has just finished
            // drawing the window, that is a visible stall at exactly the wrong moment.
            if (!await Task.Run(dictation.ShouldOfferModelDownload))
                return;

            // Recorded before the wizard rather than after it: closing it with the title bar is an
            // answer too, and a question that returns on every launch is one people learn to dismiss.
            dictation.MarkModelPromptAnswered();
            await SpeechSetupWizard.ShowAsync(this, dictation, _settingsService!);
        }
        catch (Exception ex)
        {
            // A first-run nicety must never be the reason a window fails to come up.
            System.Diagnostics.Trace.TraceWarning("Offering a speech model failed: {0}", ex);
        }
    }

    /// <summary>Completes once the window is on screen; false if it closed before that.</summary>
    private Task<bool> WhenOpenedAsync()
    {
        if (IsLoaded)
            return Task.FromResult(true);

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, EventArgs e) { Detach(); opened.TrySetResult(true); }
        void OnClosed(object? sender, EventArgs e) { Detach(); opened.TrySetResult(false); }
        void Detach() { Opened -= OnOpened; Closed -= OnClosed; }

        Opened += OnOpened;
        Closed += OnClosed;
        return opened.Task;
    }

    /// <summary>What is on screen now, or null. One dialog at a time: a held shortcut repeats, and one
    /// dialog per repeat is a wall of them.</summary>
    private string? _dictationErrorOnScreen;

    /// <summary>A different message that arrived while one was up, kept until it can be shown.</summary>
    /// <remarks>
    /// The flag alone dropped it, which is right for the repeat it was written for and wrong for
    /// everything else: "the microphone could not be opened" followed by "the transcript could not be
    /// delivered" are two different things to fix, and the second one vanished because the first was
    /// still waiting to be dismissed. One deep and last-one-wins — a queue would make the user dismiss a
    /// history of failures.
    /// </remarks>
    private string? _pendingDictationError;

    /// <summary>Dictation reports its own failures — no microphone, no model, a refused device — and
    /// they have nowhere else to appear: the button that started it may be off-screen by now.</summary>
    private async void ShowDictationError(string message)
    {
        if (_dictationErrorOnScreen is { } showing)
        {
            if (showing != message)
                _pendingDictationError = message;
            return;
        }

        // The setup wizard is up, and it is modal: a box owned by this window would open behind it, where
        // it can be neither read nor dismissed. It subscribes to the same event and shows the message in
        // its own body, so standing down for it loses nothing.
        //
        // For that window only. "Any owned window" also covered the input dialog, the update prompt and
        // every message box the Git tile raises — none of which say a word about dictation, so a failure
        // arriving while one of them was open disappeared entirely.
        if (OwnedWindows.Any(w => w is SpeechSetupWizard))
        {
            System.Diagnostics.Trace.TraceWarning(
                "A dictation message was left to the setup wizard: {0}", message);
            return;
        }

        try
        {
            _dictationErrorOnScreen = message;
            var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(
                "Dictation", message, MsBox.Avalonia.Enums.ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Warning);
            await box.ShowWindowDialogAsync(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Showing the dictation error failed: {0}", ex);
        }
        finally
        {
            _dictationErrorOnScreen = null;
        }

        // Whatever arrived while that one was up, now that there is somewhere to put it. After the
        // finally, so the next call does not find this one still on screen.
        if (_pendingDictationError is { } waiting)
        {
            _pendingDictationError = null;
            ShowDictationError(waiting);
        }
    }

    private void SwitchWorkspaceView(WorkspaceViewModel? workspace)
    {
        if (_activeWorkspaceView != null)
            _activeWorkspaceView.IsVisible = false;

        if (workspace == null)
        {
            _activeWorkspaceView = null;
            return;
        }

        if (!_viewCache.TryGetValue(workspace.WorkspaceId, out var view))
        {
            view = new WorkspaceView { DataContext = workspace };
            _viewCache[workspace.WorkspaceId] = view;
            WorkspaceHost.Children.Add(view);
        }

        view.IsVisible = true;
        _activeWorkspaceView = view;
        Dispatcher.UIThread.Post(() => workspace.FocusActiveTile(), DispatcherPriority.Input);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { IsSettingsOpen: true } vm)
        {
            // Innermost first. With an entry form open over the settings dialog, Escape means "not this
            // entry" — closing the whole dialog would throw away the form *and* the page behind it, and
            // leave the user wondering which of the two they had just cancelled.
            if (vm.Settings.IsEditingAnything)
            {
                vm.Settings.CancelEditing();
                e.Handled = true;
                return;
            }

            // Through the view model, like the close button and the click outside: it may ask about
            // unapplied database settings first, and it may answer no.
            _ = vm.CloseSettingsAsync();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void SettingsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            _ = vm.CloseSettingsAsync();
    }

    private void SettingsDialog_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void UpdateSettingsDialogSize()
    {
        if (SettingsDialog == null) return;
        var bounds = ClientSize;
        SettingsDialog.Width = Math.Max(420, bounds.Width * 0.5);
        SettingsDialog.Height = Math.Max(400, bounds.Height * 0.8);
    }

    /// <summary>Set once the user has answered the question below, so the second close does not ask
    /// again — and cannot loop.</summary>
    private bool _shutdownConfirmed;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Asking is asynchronous and closing is not, so the only way to put a question here is to call
        // the close off, wait for the answer, and close again.
        if (!_shutdownConfirmed && DataContext is MainWindowViewModel { Settings.HasUnsavedDatabaseChanges: true } asking)
        {
            e.Cancel = true;
            _ = AskThenCloseAsync(asking);
            return;
        }

        base.OnClosing(e);
        SaveWindowState();

        // Before the view models go: the shortcut's handlers are static and hold this window, the
        // dictation service and the callback that resolves the active tile. Nothing else lets go of them.
        Services.Speech.DictationHotkeys.Detach();

        if (_dictation is not null && _onDictationError is not null)
            _dictation.Error -= _onDictationError;

        if (DataContext is MainWindowViewModel vm)
            vm.DisposeAll();
    }

    private async Task AskThenCloseAsync(MainWindowViewModel vm)
    {
        if (!await vm.ConfirmShutdownAsync())
            return;

        _shutdownConfirmed = true;
        Close();
    }

    private void SaveWindowState()
    {
        if (_settingsService == null) return;
        var s = _settingsService.Settings;

        s.WindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }

        if (_panelColumn != null && _panelColumn.Width.Value > 0)
            s.WorkspacesPanelWidth = _panelColumn.Width.Value;

        _settingsService.Save();
    }
}
