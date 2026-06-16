using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MTerminal.Models;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class LeafTileView : UserControl
{
    private object? _currentContentVm;
    private string _originalTileName = "";
    private LeafTileNodeViewModel? _subscribedLeaf;

    public LeafTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedLeaf != null)
            _subscribedLeaf.PropertyChanged -= OnLeafPropertyChanged;

        if (DataContext is LeafTileNodeViewModel leaf)
        {
            _subscribedLeaf = leaf;
            leaf.PropertyChanged += OnLeafPropertyChanged;
            UpdateContentDisplay(leaf);
        }
    }

    private void OnLeafPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LeafTileNodeViewModel.Content) or nameof(LeafTileNodeViewModel.ContentType))
        {
            if (sender is LeafTileNodeViewModel leaf)
                UpdateContentDisplay(leaf);
        }
    }

    private void UpdateContentDisplay(LeafTileNodeViewModel leaf)
    {
        if (leaf.ContentType == TileContentType.Empty)
        {
            ContentChooser.IsVisible = true;
            ContentHost.IsVisible = false;
            ContentHost.Children.Clear();
            _currentContentVm = null;
        }
        else
        {
            ContentChooser.IsVisible = false;
            ContentHost.IsVisible = true;
            SetContent(leaf.Content);
        }
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
            _ => throw new InvalidOperationException($"Unknown content type: {contentVm.GetType()}")
        };

        ContentHost.Background = contentVm switch
        {
            TerminalTileViewModel t => new SolidColorBrush(Color.Parse(t.Theme.Background)),
            _ => this.FindBrush("BgBase") as SolidColorBrush
        };

        ContentHost.Children.Add(view);
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

        if (DataContext is LeafTileNodeViewModel leaf && string.IsNullOrWhiteSpace(leaf.TileName))
            leaf.TileName = _originalTileName;

        TileNameEditor.IsVisible = false;
        TileNameLabel.IsVisible = true;
    }
}
