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

public partial class GitTileView : UserControl, IFocusTargetView
{
    /// <summary>The list on show - changed files, or the history - never the commit message.</summary>
    /// <remarks>The file list is where this tile is driven from the keyboard (arrows, Space to tick);
    /// a caret parked in the message box would take every keystroke meant for it, and a click on the
    /// tile's background would drop stray letters into the next commit.</remarks>
    public Avalonia.Input.InputElement? PreferredFocusTarget =>
        FocusTargets.ListTarget(HistoryListBox.IsVisible ? HistoryListBox : FilesListBox);

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
        // Suggestions are taken by Enter or by a click, never by the selection moving: arrow keys
        // change the selection too, so committing on SelectionChanged made the first Down key the
        // whole gesture and left the list impossible to browse from the keyboard.
        SuggestionsListBox.AddHandler(PointerReleasedEvent, OnSuggestionPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Bubble);
        SuggestionsListBox.KeyDown += OnSuggestionsKeyDown;
        CommitSummaryBox.KeyDown += OnCommitSummaryKeyDown;
        // However the popup was opened — the button, which takes the focus itself, or Down out of
        // the message box — the list is what the arrows have to reach, so it takes the keyboard as
        // it appears. Escape and Tab hand it back to the message box.
        CommitSuggestionsPopup.Opened += OnSuggestionsPopupOpened;
    }

    private void OnSuggestionsPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(FocusSuggestions, DispatcherPriority.Loaded);

    /// <summary>Puts the keyboard on the first suggestion, if there is one.</summary>
    private void FocusSuggestions()
    {
        if (SuggestionsListBox.ItemCount == 0) return;
        if (DataContext is not GitTileViewModel { ShowCommitSuggestions: true }) return;

        if (SuggestionsListBox.SelectedIndex < 0)
            SuggestionsListBox.SelectedIndex = 0;
        SuggestionsListBox.Focus(NavigationMethod.Tab);
    }

    /// <summary>Down out of the message box steps into the suggestions, when they are showing.</summary>
    private void OnCommitSummaryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;
        if (DataContext is not GitTileViewModel { ShowCommitSuggestions: true }) return;
        if (SuggestionsListBox.ItemCount == 0) return;

        FocusSuggestions();
        e.Handled = true;
    }

    private void OnSuggestionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TakeSuggestion(SuggestionsListBox.SelectedItem as string);
            e.Handled = true;
            return;
        }

        // Escape and Tab both leave the list, and both put the caret back where it came from: a
        // light-dismissed popup leaves focus on a control that is no longer on screen.
        if (e.Key is not (Key.Escape or Key.Tab)) return;

        CloseSuggestions();
        e.Handled = true;
    }

    /// <remarks>What is taken is the row under the pointer, never the selected one: the list
    /// scrolls, so a release that ends on its scrollbar — or anywhere but a row — would otherwise
    /// commit whatever the arrow keys had highlighted, or close the popup outright.</remarks>
    private void OnSuggestionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not { DataContext: string message })
            return;

        TakeSuggestion(message);
    }

    private void TakeSuggestion(string? message)
    {
        if (message is null || DataContext is not GitTileViewModel vm)
        {
            CloseSuggestions();
            return;
        }

        vm.SelectCommitSuggestionCommand.Execute(message);
        SuggestionsListBox.SelectedIndex = -1;
        CommitSummaryBox.Focus();
        CommitSummaryBox.CaretIndex = CommitSummaryBox.Text?.Length ?? 0;
    }

    private void CloseSuggestions()
    {
        if (DataContext is GitTileViewModel vm)
            vm.ShowCommitSuggestions = false;
        SuggestionsListBox.SelectedIndex = -1;
        CommitSummaryBox.Focus();
    }

    private void OnFilesListKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryOpenMenuFromKeyboard(e)) return;
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

    /// <summary>Opens the context menu of whichever list has the keyboard.</summary>
    /// <remarks>The Menu key and Shift+F10 are the platform's own gesture for it, on Windows and on
    /// every Linux desktop alike; without them every action in these two menus needed a mouse.
    /// Anchored on the selected row, so the menu opens where the selection is rather than at the
    /// pointer, which may be anywhere at all.</remarks>
    private bool TryOpenMenuFromKeyboard(KeyEventArgs e)
    {
        if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            return false;
        if (DataContext is not GitTileViewModel vm) return false;

        var list = HistoryListBox.IsVisible && HistoryListBox.IsKeyboardFocusWithin ? HistoryListBox
            : FilesListBox.IsKeyboardFocusWithin ? FilesListBox
            : null;
        if (list is null || list.SelectedItem is null) return false;

        if (list.ContainerFromItem(list.SelectedItem) is not ListBoxItem item) return false;

        if (item.DataContext is GitFileChange change)
            OpenFilesMenu(vm, item, change);
        else if (item.DataContext is CommitLogEntry commit)
            OpenHistoryMenu(vm, item, commit);
        else
            return false;

        e.Handled = true;
        return true;
    }

    private void OnFilesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not GitTileViewModel vm) return;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not GitFileChange change) return;

        OpenFilesMenu(vm, item, change);
        e.Handled = true;
    }

    /// <summary>The changed files' menu, wherever it was asked for.</summary>
    /// <remarks>Split out of the right-click handler so the keyboard can reach it: the Menu key and
    /// Shift+F10 are how this list is used without a mouse, and a menu only a right-click can open is
    /// a set of actions with no keyboard route at all.</remarks>
    private void OpenFilesMenu(GitTileViewModel vm, ListBoxItem item, GitFileChange change)
    {
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

        OpenHistoryMenu(vm, item, commit);
        e.Handled = true;
    }

    /// <summary>The history's menu — see <see cref="OpenFilesMenu"/> for why it is not inline.</summary>
    private void OpenHistoryMenu(GitTileViewModel vm, ListBoxItem item, CommitLogEntry commit)
    {
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
