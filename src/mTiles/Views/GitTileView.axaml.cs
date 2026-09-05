using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class GitTileView : UserControl
{
    private bool _isVerticalLayout;
    private bool _layoutApplied;
    private GitTileViewModel? _subscribedVm;

    public GitTileView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnFilesListKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        FilesListBox.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnFilesPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HistoryListBox.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnHistoryPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SuggestionsListBox.SelectionChanged += OnSuggestionSelected;
    }

    private void OnFilesListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (!FilesListBox.IsFocused && !FilesListBox.IsKeyboardFocusWithin) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        if (DataContext is not GitTileViewModel vm) return;

        var selected = FilesListBox.SelectedItems;
        if (selected is not { Count: > 0 }) return;

        var files = selected.OfType<GitFileChange>().ToList();
        var newState = !files.All(f => f.IsChecked);
        foreach (var file in files)
            file.IsChecked = newState;

        e.Handled = true;
    }

    private void OnFilesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not GitTileViewModel vm) return;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not GitFileChange change) return;

        var selected = FilesListBox.SelectedItems?.OfType<GitFileChange>().ToList() ?? [];
        if (!selected.Contains(change))
            selected = [change];

        var isMulti = selected.Count > 1;
        object discardParam = isMulti ? selected : change;
        var discardHeader = isMulti ? $"Discard changes ({selected.Count} files)" : "Discard changes";

        var menu = new ContextMenu();
        if (!isMulti)
        {
            menu.Items.Add(new MenuItem { Header = "Show in Explorer", Command = vm.ShowInExplorerCommand, CommandParameter = change });
            menu.Items.Add(new MenuItem { Header = "Open in default program", Command = vm.OpenInDefaultProgramCommand, CommandParameter = change });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Copy filename", Command = vm.CopyFilenameCommand, CommandParameter = change });
            menu.Items.Add(new MenuItem { Header = "Copy folder", Command = vm.CopyFolderCommand, CommandParameter = change });
            menu.Items.Add(new MenuItem { Header = "Copy filepath", Command = vm.CopyFilepathCommand, CommandParameter = change });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Header = discardHeader, Command = vm.DiscardChangesCommand, CommandParameter = discardParam });

        menu.Open(item);
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is not GitTileViewModel vm) return;

        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.GetClipboard = () => TopLevel.GetTopLevel(this)?.Clipboard;
        vm.ConfirmAction = message =>
            MessageDialog.ConfirmAsync(this, "Confirm", message, whenUnavailable: true);

        vm.PromptInput = (title, placeholder, suggestions) =>
            InputDialog.ShowAsync(this, title, placeholder, suggestions);

        vm.ShowError = (title, message) =>
            MessageDialog.ShowAsync(this, title, message, MessageDialog.Tone.Error);

        FontFamily = new FontFamily(vm.FontFamily);
        FontSize = vm.FontSize;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GitTileViewModel vm) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(GitTileViewModel.FontFamily):
                    FontFamily = new FontFamily(vm.FontFamily);
                    break;
                case nameof(GitTileViewModel.FontSize):
                    FontSize = vm.FontSize;
                    break;
                case nameof(GitTileViewModel.ShowDiffPanel):
                case nameof(GitTileViewModel.SelectedChange):
                    RefreshLayout();
                    break;
            }
        });
    }

    private const double MinDiffSize = 250;
    private const double SidebarSize = 260;
    private const double MinCommitAreaHeight = 200;

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _lastSize = e.NewSize;
        RefreshLayout();
    }

    private Size _lastSize;

    private void RefreshLayout()
    {
        var w = _lastSize.Width;
        var h = _lastSize.Height;
        if (w == 0 && h == 0) return;

        var wantVertical = h > w;
        var layoutChanged = !_layoutApplied || wantVertical != _isVerticalLayout;
        _isVerticalLayout = wantVertical;
        _layoutApplied = true;

        var vm = DataContext as GitTileViewModel;
        var userWantsDiff = vm?.ShowDiffPanel ?? true;
        var hasSelection = vm?.SelectedChange != null;

        bool showDiff;
        if (!userWantsDiff || !hasSelection)
            showDiff = false;
        else if (_isVerticalLayout)
            showDiff = h > MinDiffSize + MinDiffSize;
        else
            showDiff = w > SidebarSize + MinDiffSize;

        if (RightPanel.IsVisible != showDiff || MainSplitter.IsVisible != showDiff)
        {
            RightPanel.IsVisible = showDiff;
            MainSplitter.IsVisible = showDiff;
            layoutChanged = true;
        }

        var sidebarHeight = (_isVerticalLayout && showDiff) ? h / 2 : h;
        var showCommit = sidebarHeight > MinCommitAreaHeight;
        if (CommitArea.IsVisible != showCommit)
            CommitArea.IsVisible = showCommit;

        if (layoutChanged)
            ApplyLayout(showDiff);
    }

    private void OnHistoryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not GitTileViewModel vm) return;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not CommitLogEntry commit) return;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "Add tag...",
            Command = vm.CreateTagCommand,
            CommandParameter = commit
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Copy commit hash",
            Command = vm.CopyCommitHashCommand,
            CommandParameter = commit
        });
        menu.Open(item);
        e.Handled = true;
    }

    private void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is not { Count: > 0 }) return;
        if (e.AddedItems[0] is not string msg) return;
        if (DataContext is not GitTileViewModel vm) return;

        vm.SelectCommitSuggestionCommand.Execute(msg);
        SuggestionsListBox.SelectedIndex = -1;
    }

    private void ApplyLayout(bool showDiff)
    {
        MainGrid.ColumnDefinitions.Clear();
        MainGrid.RowDefinitions.Clear();

        if (_isVerticalLayout)
        {
            MainGrid.RowDefinitions.Add(new RowDefinition(showDiff ? new GridLength(1, GridUnitType.Star) : GridLength.Star) { MinHeight = 180 });
            MainGrid.RowDefinitions.Add(new RowDefinition(showDiff ? GridLength.Auto : new GridLength(0)));
            MainGrid.RowDefinitions.Add(new RowDefinition(showDiff ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));

            Grid.SetColumn(Sidebar, 0);       Grid.SetRow(Sidebar, 0);
            Grid.SetColumn(MainSplitter, 0);  Grid.SetRow(MainSplitter, 1);
            Grid.SetColumn(RightPanel, 0);    Grid.SetRow(RightPanel, 2);

            MainSplitter.Width = double.NaN;
            MainSplitter.Height = 3;
            MainSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(showDiff ? new GridLength(SidebarSize, GridUnitType.Pixel) : GridLength.Star));
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(showDiff ? GridLength.Auto : new GridLength(0)));
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(showDiff ? GridLength.Star : new GridLength(0)));

            Grid.SetRow(Sidebar, 0);       Grid.SetColumn(Sidebar, 0);
            Grid.SetRow(MainSplitter, 0);  Grid.SetColumn(MainSplitter, 1);
            Grid.SetRow(RightPanel, 0);    Grid.SetColumn(RightPanel, 2);

            MainSplitter.Height = double.NaN;
            MainSplitter.Width = 3;
            MainSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
    }
}
