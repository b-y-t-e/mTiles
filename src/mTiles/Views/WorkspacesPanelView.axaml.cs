using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
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
                // Under the two harmless ones and above Remove, which is where its weight puts it: it
                // stops whatever the workspace is running, and it does not lose the workspace.
                new MenuItem
                {
                    Header = "Unload",
                    Command = vm.UnloadWorkspaceCommand,
                    CommandParameter = item
                },
                BuildAgentFileSyncMenuItem(vm, item),
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

    /// <summary>The row that switches the two instruction files' mirror on and off.</summary>
    /// <remarks>
    /// <para><b>The state is one mark, and it is the icon.</b> A checkbox and a struck-through arrow in
    /// the middle of the text said the same thing twice, and neither said it well: two marks for one
    /// fact leave the reader deciding which to believe, and at the size a menu row is drawn the
    /// difference between <c>⇄</c> and <c>⇹</c> is two hairlines — a state you have to go and look for
    /// is not a state the row reports. So the text is now the same either way, and what changes is the
    /// glyph in the column a menu keeps for exactly this, in the application's own icon vocabulary: a
    /// link, or a link broken.</para>
    /// <para>Colour carries it the rest of the way — lit while the two files are being kept identical,
    /// muted while they are free to drift — bound rather than resolved once, so it follows a theme
    /// change like every other colour here.</para>
    /// </remarks>
    private static MenuItem BuildAgentFileSyncMenuItem(WorkspacesPanelViewModel vm, WorkspaceItemViewModel item)
    {
        var canToggle = vm.CanToggleAgentFileSync;
        var isOn = vm.IsAgentFileSyncEnabled(item);

        var icon = new Material.Icons.Avalonia.MaterialIcon
        {
            Kind = isOn
                ? Material.Icons.MaterialIconKind.LinkVariant
                : Material.Icons.MaterialIconKind.LinkVariantOff,
            Width = 14,
            Height = 14
        };
        icon.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            icon.GetResourceObservable(isOn ? "AccentDefault" : "TextFaint").ToBinding());

        var menuItem = new MenuItem
        {
            Header = "CLAUDE.md ⇄ AGENTS.md",
            Icon = icon,
            IsChecked = isOn,
            IsEnabled = canToggle,
            Command = vm.ToggleAgentFileSyncCommand,
            CommandParameter = item
        };
        if (!canToggle)
            ToolTip.SetTip(menuItem, "Enable this in Settings → General first");
        return menuItem;
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

            vm.ShowError = (title, message) =>
                MessageDialog.ShowAsync(this, title, message, MessageDialog.Tone.Error);
            vm.ConfirmAction = message =>
                MessageDialog.ConfirmAsync(this, "Confirm", message, whenUnavailable: true);
            vm.RevealWorkspaceRequested = RevealWorkspace;

            // The workspace restored from the last session is selected before this view exists, so
            // nothing asked for it to be scrolled to — on a list longer than the panel the application
            // opened with the one row the user is looking for below the fold, and the highlight they
            // would have found it by is exactly what was off screen.
            if (vm.SelectedWorkspace is { } restored)
                RevealWorkspace(restored);
        }
    }

    /// <summary>Scrolls a row into view, in whichever of the two lists is on screen.</summary>
    /// <remarks>Posted at <see cref="DispatcherPriority.Loaded"/> because the row is asked for in the
    /// same breath as it is added: the container does not exist until the layout pass that follows, and
    /// <c>ContainerFromItem</c> answers null until it does. The collapsed list is asked as well rather
    /// than instead — the panel can be either shape when a workspace is added, and a container that is
    /// not there costs a null.</remarks>
    private void RevealWorkspace(WorkspaceItemViewModel item) => RevealWorkspace(item, attempt: 0);

    /// <remarks>Tried twice. At startup the panel is asked for a row before it has been laid out at
    /// all — the restored workspace is selected in the view model's constructor — and a container that
    /// does not exist yet answers null exactly like a row that is not there. One more pass, at a lower
    /// priority, is after the layout it was waiting for; a second failure is a row that genuinely is
    /// not in the list (filtered out, or already removed), and retrying that for ever would be a
    /// dispatcher loop nobody can see.</remarks>
    private void RevealWorkspace(WorkspaceItemViewModel item, int attempt) =>
        Dispatcher.UIThread.Post(() =>
        {
            var list = _isCollapsed ? CollapsedWorkspaceList : (ItemsControl)WorkspaceList;
            var container = list.ContainerFromItem(item);
            if (container is not null)
            {
                container.BringIntoView();
                return;
            }

            if (attempt == 0)
                RevealWorkspace(item, attempt + 1);
        }, attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Background);

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
