using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Views;

public partial class LeafTileView : UserControl
{
    private object? _currentContentVm;
    private string _originalTileName = "";
    private LeafTileNodeViewModel? _subscribedLeaf;
    private INotifyPropertyChanged? _subscribedContent;
    private Point? _dragStartPoint;
    private PointerPressedEventArgs? _dragPressedArgs;

    public LeafTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(InputElement.KeyDownEvent, OnTileKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, OnTilePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.GotFocusEvent, OnTileGotFocus, Avalonia.Interactivity.RoutingStrategies.Bubble);

        TileToolbar.AddHandler(InputElement.PointerPressedEvent, OnToolbarPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TileToolbar.AddHandler(InputElement.PointerMovedEvent, OnToolbarPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TileToolbar.AddHandler(InputElement.PointerReleasedEvent, OnToolbarPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DropOverlay.BorderThickness = new Thickness(2);
    }

    private void OnTilePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        (DataContext as LeafTileNodeViewModel)?.Activate();
    }

    private void OnTileGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as LeafTileNodeViewModel)?.Activate();
    }

    private void OnTileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (DataContext is LeafTileNodeViewModel { ContentType: TileContentType.Terminal } leaf)
            {
                leaf.RestartTerminalCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedLeaf != null)
        {
            _subscribedLeaf.PropertyChanged -= OnLeafPropertyChanged;
            _subscribedLeaf.FocusRequested -= FocusContent;
        }

        if (DataContext is LeafTileNodeViewModel leaf)
        {
            _subscribedLeaf = leaf;
            leaf.PropertyChanged += OnLeafPropertyChanged;
            leaf.FocusRequested += FocusContent;
            leaf.ConfirmAction = async message =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return true;
                var box = MessageBoxManager.GetMessageBoxStandard(
                    "Confirm", message, ButtonEnum.YesNo, Icon.Question);
                var result = await box.ShowWindowDialogAsync(window);
                return result == ButtonResult.Yes;
            };
            UpdateActiveIndicator(leaf.IsActive);
            UpdateContentDisplay(leaf);
        }
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not LeafTileNodeViewModel leaf) return;

        if (e.PropertyName is nameof(LeafTileNodeViewModel.Content) or nameof(LeafTileNodeViewModel.ContentType))
            UpdateContentDisplay(leaf);
        else if (e.PropertyName == nameof(LeafTileNodeViewModel.IsActive))
            UpdateActiveIndicator(leaf.IsActive);
        else if (e.PropertyName == nameof(LeafTileNodeViewModel.IsChoosingProfile))
            UpdateChooserVisibility(leaf);
    }

    private void UpdateActiveIndicator(bool isActive)
    {
        ActiveStrip.Bind(Border.BackgroundProperty,
            ActiveStrip.GetResourceObservable(isActive ? "AccentHover" : "BgSurface"));
        TileToolbar.Bind(Border.BackgroundProperty,
            TileToolbar.GetResourceObservable(isActive ? "BgElevated" : "BgSurface"));
    }

    private void UpdateContentDisplay(LeafTileNodeViewModel leaf)
    {
        if (leaf.ContentType == TileContentType.Empty)
        {
            ContentChooser.IsVisible = !leaf.IsChoosingProfile;
            ProfileChooser.IsVisible = leaf.IsChoosingProfile;
            if (leaf.IsChoosingProfile)
                PopulateProfileButtons(leaf);
            ContentHost.IsVisible = false;
            ContentHost.Children.Clear();
            _currentContentVm = null;
        }
        else
        {
            ContentChooser.IsVisible = false;
            ProfileChooser.IsVisible = false;
            ContentHost.IsVisible = true;
            SetContent(leaf.Content);
        }
    }

    private void UpdateChooserVisibility(LeafTileNodeViewModel leaf)
    {
        if (leaf.ContentType != TileContentType.Empty) return;
        ContentChooser.IsVisible = !leaf.IsChoosingProfile;
        ProfileChooser.IsVisible = leaf.IsChoosingProfile;
        if (leaf.IsChoosingProfile)
            PopulateProfileButtons(leaf);
    }

    private void PopulateProfileButtons(LeafTileNodeViewModel leaf)
    {
        var markerIndex = ProfileChooser.Children.IndexOf(ProfileButtonsMarker);
        if (markerIndex < 0) return;
        while (ProfileChooser.Children.Count > markerIndex + 1)
            ProfileChooser.Children.RemoveAt(ProfileChooser.Children.Count - 1);

        var profiles = leaf.AvailableProfiles;
        if (profiles == null) return;

        foreach (var profile in profiles)
        {
            var icon = new MaterialIcon
            {
                Kind = MaterialIconKind.ScriptOutline, Width = 22, Height = 22,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("TextMuted"));

            var label = new TextBlock
            {
                Text = profile.Name,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            label.Bind(TextBlock.FontSizeProperty, label.GetResourceObservable("FontSm"));

            var accent = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Margin = new Thickness(-16, 0, 0, 0),
            };
            accent.Bind(Border.BackgroundProperty, accent.GetResourceObservable("TileAccentTerminal"));

            var btn = new Button { Classes = { "chooser-card" } };
            btn.Command = leaf.SelectProfileCommand;
            btn.CommandParameter = profile;
            btn.Content = new Grid
            {
                Children =
                {
                    accent,
                    new StackPanel
                    {
                        Spacing = 4,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Children = { icon, label }
                    }
                }
            };
            ProfileChooser.Children.Add(btn);
        }
    }

    private void SetContent(object? contentVm)
    {
        if (contentVm == _currentContentVm && ContentHost.Children.Count > 0)
            return;

        _currentContentVm = contentVm;

        var suspended = ControlHelper.SuspendTerminals(ContentHost);
        ContentHost.Children.Clear();

        if (contentVm == null)
        {
            ControlHelper.ResumeTerminals(suspended);
            return;
        }

        UserControl view = contentVm switch
        {
            TerminalTileViewModel => new TerminalTileView { DataContext = contentVm },
            NoteTileViewModel => new NoteTileView { DataContext = contentVm },
            TodoTileViewModel => new TodoTileView { DataContext = contentVm },
            GitTileViewModel => new GitTileView { DataContext = contentVm },
            DatabaseTileViewModel => new DatabaseTileView { DataContext = contentVm },
            GoalTileViewModel => new GoalTileView { DataContext = contentVm },
            _ => throw new InvalidOperationException($"Unknown content type: {contentVm.GetType()}")
        };

        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged -= OnContentPropertyChanged;
        _subscribedContent = contentVm as INotifyPropertyChanged;
        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged += OnContentPropertyChanged;

        UpdateContentBackground(contentVm);
        ContentHost.Children.Add(view);
        ControlHelper.ResumeTerminals(suspended);
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTileViewModel.Theme))
            UpdateContentBackground(sender);
    }

    private void UpdateContentBackground(object? contentVm)
    {
        if (contentVm is TerminalTileViewModel t)
            ContentHost.Background = new SolidColorBrush(Color.Parse(t.Theme.Background));
        else
            ContentHost.Bind(Panel.BackgroundProperty, ContentHost.GetResourceObservable("BgBase"));
    }

    private void TileNameLabel_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LeafTileNodeViewModel leaf)
            _originalTileName = leaf.TileName;

        TileNameLabel.IsVisible = false;
        TileNameEditor.IsVisible = true;
        TileNameEditor.Focus();
        TileNameEditor.SelectAll();
    }

    private void TileNameEditor_Confirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CommitRename();
    }

    private void TileNameEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is LeafTileNodeViewModel leaf)
                leaf.TileName = _originalTileName;
            TileNameEditor.IsVisible = false;
            TileNameLabel.IsVisible = true;
            e.Handled = true;
        }
    }

    private void CommitRename()
    {
        if (!TileNameEditor.IsVisible) return;

        if (DataContext is LeafTileNodeViewModel leaf)
        {
            if (string.IsNullOrWhiteSpace(leaf.TileName))
                leaf.TileName = _originalTileName;

            if (leaf.TileName != _originalTileName)
                (leaf.Content as IFileContent)?.RenameFile(leaf.TileName);
        }

        TileNameEditor.IsVisible = false;
        TileNameLabel.IsVisible = true;
    }

    // Suppress activation during Focus() to prevent GotFocus → Activate → FocusContent ping-pong
    private void FocusContent()
    {
        if (_subscribedLeaf == null) return;
        using var _ = _subscribedLeaf.ActivationScope.SuppressActivation();
        var focusable = ContentHost.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(e => e.Focusable);
        focusable?.Focus();
    }

    #region Drag & Drop

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsInsideButton(e.Source as Control)) return;
        if (TileNameEditor.IsVisible) return;
        _dragStartPoint = e.GetPosition(this);
        _dragPressedArgs = e;
    }

    private async void OnToolbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint == null || _dragPressedArgs == null) return;

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        var pressedArgs = _dragPressedArgs;
        _dragStartPoint = null;
        _dragPressedArgs = null;

        if (DataContext is not LeafTileNodeViewModel leaf) return;

        TileDragDrop.DragSource = leaf;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(TileDragDrop.DataFormat));

        Opacity = 0.4;
        try
        {
            await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("DragDrop failed: {0}", ex.Message);
        }
        finally
        {
            Opacity = 1.0;
            TileDragDrop.DragSource = null;
        }
    }

    private void OnToolbarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _dragPressedArgs = null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var source = TileDragDrop.DragSource;
        if (source == null || source == DataContext)
        {
            e.DragEffects = DragDropEffects.None;
            HideDropOverlay();
            return;
        }

        var pos = e.GetPosition(this);
        var zone = TileDragDrop.GetDropZone(pos, Bounds.Size);
        ShowDropOverlay(zone);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();

        var source = TileDragDrop.DragSource;
        var target = DataContext as LeafTileNodeViewModel;
        if (source == null || target == null || source == target) return;

        var pos = e.GetPosition(this);
        var zone = TileDragDrop.GetDropZone(pos, Bounds.Size);
        TileDragDrop.Execute(source, target, zone);
        e.Handled = true;
    }

    private void ShowDropOverlay(DropZone zone)
    {
        if (zone == DropZone.None) { HideDropOverlay(); return; }

        var accent = this.FindResource("AccentHover") as ISolidColorBrush;
        var accentColor = accent?.Color ?? Color.FromRgb(0x3a, 0x6f, 0xa0);
        var fillBrush = new SolidColorBrush(Color.FromArgb(55, accentColor.R, accentColor.G, accentColor.B));
        var borderBrush = new SolidColorBrush(Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B));

        var w = Bounds.Width;
        var h = Bounds.Height;

        if (zone == DropZone.Center)
        {
            DropOverlay.Background = Brushes.Transparent;
            DropOverlay.BorderBrush = borderBrush;
            DropOverlay.BorderThickness = new Thickness(3);
            DropOverlay.Margin = new Thickness(3);
        }
        else
        {
            DropOverlay.Background = fillBrush;
            DropOverlay.BorderBrush = borderBrush;
            DropOverlay.BorderThickness = new Thickness(2);
            DropOverlay.Margin = zone switch
            {
                DropZone.Left   => new Thickness(2, 2, w * 0.70, 2),
                DropZone.Right  => new Thickness(w * 0.70, 2, 2, 2),
                DropZone.Top    => new Thickness(2, 2, 2, h * 0.70),
                DropZone.Bottom => new Thickness(2, h * 0.70, 2, 2),
                _ => default
            };
        }
        DropOverlay.IsVisible = true;
    }

    private void HideDropOverlay()
    {
        DropOverlay.IsVisible = false;
    }

    private static bool IsInsideButton(Control? control)
    {
        while (control != null)
        {
            if (control is Button) return true;
            control = control.Parent as Control;
        }
        return false;
    }

    #endregion
}
