using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class WorkspacesPanelView : UserControl
{
    private WorkspacesPanelViewModel? _subscribedVm;

    public WorkspacesPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void WorkspaceItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not WorkspacesPanelViewModel vm) return;

        var border = sender as Border;
        if (border?.DataContext is not WorkspaceItemViewModel item) return;

        if (e.GetCurrentPoint(border).Properties.IsRightButtonPressed)
        {
            ShowContextMenu(vm, item, border);
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
