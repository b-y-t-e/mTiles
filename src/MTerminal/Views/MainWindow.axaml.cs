using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MTerminal.Services;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class MainWindow : Window
{
    private SettingsService? _settingsService;
    private ColumnDefinition? _panelColumn;
    private double _lastPanelWidth = 240;
    private readonly Dictionary<string, WorkspaceView> _viewCache = new();
    private WorkspaceView? _activeWorkspaceView;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateSettingsDialogSize();
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

        _lastPanelWidth = s.WorkspacesPanelWidth;
        _panelColumn.Width = new GridLength(_lastPanelWidth, GridUnitType.Pixel);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsPanelOpen))
                    UpdatePanelVisibility(vm.IsPanelOpen);
                else if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
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
            UpdatePanelVisibility(vm.IsPanelOpen);
            SwitchWorkspaceView(vm.CurrentWorkspace);
        }
    }

    private void UpdatePanelVisibility(bool isOpen)
    {
        if (_panelColumn == null) return;
        if (isOpen)
        {
            _panelColumn.Width = new GridLength(_lastPanelWidth, GridUnitType.Pixel);
            _panelColumn.MinWidth = 150;
            _panelColumn.MaxWidth = 500;
            PanelSplitter.IsVisible = true;
        }
        else
        {
            if (_panelColumn.Width.Value > 0)
                _lastPanelWidth = _panelColumn.Width.Value;
            _panelColumn.Width = new GridLength(0);
            _panelColumn.MinWidth = 0;
            _panelColumn.MaxWidth = 0;
            PanelSplitter.IsVisible = false;
        }
    }

    // Cached workspace views — toggle IsVisible instead of recreating via DataTemplate
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
            vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void SettingsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsSettingsOpen = false;
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        SaveWindowState();
        if (DataContext is MainWindowViewModel vm)
            vm.DisposeAll();
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
