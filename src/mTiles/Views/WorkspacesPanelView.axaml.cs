using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class WorkspacesPanelView : UserControl
{
    private WorkspacesPanelViewModel? _subscribedVm;
    private bool _isCollapsed;
    private const double CollapseThreshold = 80;

    public WorkspacesPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var collapsed = e.NewSize.Width < CollapseThreshold;
        if (collapsed == _isCollapsed) return;
        _isCollapsed = collapsed;
        ExpandedPanel.IsVisible = !collapsed;
        CollapsedPanel.IsVisible = collapsed;
    }

    private void WorkspaceItem_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        HandleWorkspacePointerPressed(sender, e);

    private void CollapsedItem_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        HandleWorkspacePointerPressed(sender, e);

    private void HandleWorkspacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not WorkspacesPanelViewModel vm) return;
        if (sender is not Control { DataContext: WorkspaceItemViewModel item } control) return;

        if (e.GetCurrentPoint(control).Properties.IsRightButtonPressed)
        {
            ShowContextMenu(vm, item, control);
            e.Handled = true;
            return;
        }

        vm.SelectWorkspaceCommand.Execute(item);
    }

    private void ShowContextMenu(WorkspacesPanelViewModel vm, WorkspaceItemViewModel item, Control anchor)
    {
        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "Show in Explorer",
                    Command = vm.OpenInFileManagerCommand,
                    CommandParameter = item
                },
                new MenuItem
                {
                    Header = "Copy path",
                    Command = new AsyncRelayCommand(async () =>
                    {
                        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard != null)
                            await clipboard.SetTextAsync(item.DirectoryPath);
                    })
                },
                new Separator(),
                new MenuItem
                {
                    Header = "Remove",
                    Command = vm.RemoveWorkspaceCommand,
                    CommandParameter = item
                }
            }
        };

        menu.Open(anchor);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is WorkspacesPanelViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            FontFamily = new FontFamily(vm.FontFamily);
            FontSize = vm.FontSize;
            vm.FolderPicker = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = "Select workspace directory", AllowMultiple = false });

                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };

            vm.ConfirmAction = async message =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return true;

                var box = MessageBoxManager.GetMessageBoxStandard(
                    "Confirm", message, ButtonEnum.YesNo, Icon.Question);
                var result = await box.ShowWindowDialogAsync(window);
                return result == ButtonResult.Yes;
            };
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkspacesPanelViewModel vm) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(WorkspacesPanelViewModel.FontFamily):
                    FontFamily = new FontFamily(vm.FontFamily);
                    break;
                case nameof(WorkspacesPanelViewModel.FontSize):
                    FontSize = vm.FontSize;
                    break;
            }
        });
    }
}
