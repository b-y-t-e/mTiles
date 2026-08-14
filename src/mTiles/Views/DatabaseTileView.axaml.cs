using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class DatabaseTileView : UserControl
{
    public DatabaseTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not DatabaseTileViewModel vm) return;

        vm.ConfirmAction = async message =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) return true;

            var box = MessageBoxManager.GetMessageBoxStandard(
                "Confirm", message, ButtonEnum.YesNo, Icon.Question);
            var result = await box.ShowWindowDialogAsync(window);
            return result == ButtonResult.Yes;
        };

        vm.ScrollLogsToEnd = () =>
        {
            var sv = this.FindControl<ScrollViewer>("LogScrollViewer");
            sv?.ScrollToEnd();
        };

        vm.GetClipboard = () => TopLevel.GetTopLevel(this)?.Clipboard;

        vm.OpenDatabaseSettings = () =>
        {
            var mainVm = (TopLevel.GetTopLevel(this) as Window)?.DataContext as MainWindowViewModel;
            if (mainVm == null) return;
            mainVm.Settings.SelectedTab = SettingsTabs.Database;
            mainVm.IsSettingsOpen = true;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!_statusColorApplied)
        {
            _statusColorApplied = true;
            AddHandler(Avalonia.Controls.Control.LoadedEvent, OnItemLoaded, handledEventsToo: true);
        }
    }

    private bool _statusColorApplied;

    private void OnItemLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is not TextBlock tb || tb.Name != "StatusText") return;
        if (tb.DataContext is not Services.Database.DbLogEntry entry || entry.StatusCode == null) return;

        var code = entry.StatusCode.Value;
        var colorKey = code < 400 ? "StatusSuccessText" : "DangerText";
        if (this.TryFindResource(colorKey, this.ActualThemeVariant, out var res) && res is IBrush brush)
            tb.Foreground = brush;
    }
}
