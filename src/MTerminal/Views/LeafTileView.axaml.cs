using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MTerminal.Models;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class LeafTileView : UserControl
{
    private object? _currentContentVm;
    private string _originalTileName = "";
    private LeafTileNodeViewModel? _subscribedLeaf;
    private INotifyPropertyChanged? _subscribedContent;

    public LeafTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(InputElement.KeyDownEvent, OnTileKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, OnTilePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(InputElement.GotFocusEvent, OnTileGotFocus, Avalonia.Interactivity.RoutingStrategies.Bubble);
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
    }

    private void SetContent(object? contentVm)
    {
        if (contentVm == _currentContentVm && ContentHost.Children.Count > 0)
            return;

        _currentContentVm = contentVm;
        ContentHost.Children.Clear();

        if (contentVm == null) return;

        UserControl view = contentVm switch
        {
            TerminalTileViewModel => new TerminalTileView { DataContext = contentVm },
            NoteTileViewModel => new NoteTileView { DataContext = contentVm },
            TodoTileViewModel => new TodoTileView { DataContext = contentVm },
            GitTileViewModel => new GitTileView { DataContext = contentVm },
            _ => throw new InvalidOperationException($"Unknown content type: {contentVm.GetType()}")
        };

        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged -= OnContentPropertyChanged;
        _subscribedContent = contentVm as INotifyPropertyChanged;
        if (_subscribedContent != null)
            _subscribedContent.PropertyChanged += OnContentPropertyChanged;

        UpdateContentBackground(contentVm);
        ContentHost.Children.Add(view);
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
}
