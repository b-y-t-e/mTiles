using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iciclecreek.Terminal;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class TileNodeView : UserControl
{
    private TileNodeViewModel? _vm;
    private TileNodeView? _firstChild;
    private TileNodeView? _secondChild;
    private bool _isBuilding;

    public TileNodeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_isBuilding) return;

        Detach();
        _vm = DataContext as TileNodeViewModel;
        Attach();
        Rebuild();
    }

    private void Attach()
    {
        if (_vm != null)
            _vm.PropertyChanged += OnVmChanged;
    }

    private void Detach()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is SplitTileNodeViewModel split &&
            e.PropertyName is nameof(SplitTileNodeViewModel.First)
                or nameof(SplitTileNodeViewModel.Second)
                or nameof(SplitTileNodeViewModel.Orientation))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (_isBuilding) return;
        _isBuilding = true;

        var suspended = SuspendTerminals();
        try
        {
            if (_vm is LeafTileNodeViewModel leaf)
                ShowLeaf(leaf);
            else if (_vm is SplitTileNodeViewModel split)
                ShowSplit(split);
            else
                Content = null;
        }
        finally
        {
            ResumeTerminals(suspended);
            _isBuilding = false;
        }
    }

    private void ShowLeaf(LeafTileNodeViewModel leaf)
    {
        _firstChild = null;
        _secondChild = null;

        if (Content is LeafTileView existing && existing.DataContext == leaf)
            return;

        Content = new LeafTileView { DataContext = leaf };
    }

    private void ShowSplit(SplitTileNodeViewModel split)
    {
        if (_firstChild == null) _firstChild = new TileNodeView();
        if (_secondChild == null) _secondChild = new TileNodeView();

        ControlHelper.DetachFromParent(_firstChild);
        ControlHelper.DetachFromParent(_secondChild);

        if (_firstChild.DataContext != split.First)
            _firstChild.DataContext = split.First;
        if (_secondChild.DataContext != split.Second)
            _secondChild.DataContext = split.Second;

        var grid = new Grid();
        var splitter = new GridSplitter
        {
            ResizeDirection = split.Orientation == Orientation.Vertical
                ? GridResizeDirection.Columns
                : GridResizeDirection.Rows
        };
        splitter.DragCompleted += (_, _) => UpdateSplitRatio(split, grid);

        if (split.Orientation == Orientation.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(split.SplitRatio, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(3, GridUnitType.Pixel)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - split.SplitRatio, GridUnitType.Star)));

            Grid.SetColumn(_firstChild, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_secondChild, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(split.SplitRatio, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(3, GridUnitType.Pixel)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1 - split.SplitRatio, GridUnitType.Star)));

            Grid.SetRow(_firstChild, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(_secondChild, 2);
        }

        grid.Children.Add(_firstChild);
        grid.Children.Add(splitter);
        grid.Children.Add(_secondChild);

        Content = grid;
    }

    private static void UpdateSplitRatio(SplitTileNodeViewModel split, Grid grid)
    {
        if (split.Orientation == Orientation.Vertical && grid.ColumnDefinitions.Count >= 3)
        {
            var first = grid.ColumnDefinitions[0].Width.Value;
            var second = grid.ColumnDefinitions[2].Width.Value;
            var total = first + second;
            if (total > 0)
                split.SplitRatio = first / total;
        }
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
        {
            var first = grid.RowDefinitions[0].Height.Value;
            var second = grid.RowDefinitions[2].Height.Value;
            var total = first + second;
            if (total > 0)
                split.SplitRatio = first / total;
        }
    }

    private List<TerminalControl> SuspendTerminals()
    {
        var terminals = new List<TerminalControl>();
        CollectTerminals(this, terminals);
        foreach (var tc in terminals)
            tc.BeginReparent();
        return terminals;
    }

    private static void ResumeTerminals(List<TerminalControl> terminals)
    {
        foreach (var tc in terminals)
            tc.EndReparent();
    }

    private static void CollectTerminals(Control control, List<TerminalControl> result)
    {
        if (control is TerminalControl tc)
        {
            result.Add(tc);
            return;
        }

        if (control is ContentControl cc && cc.Content is Control child)
            CollectTerminals(child, result);
        else if (control is Decorator dec && dec.Child is Control decChild)
            CollectTerminals(decChild, result);
        else if (control is Panel panel)
        {
            foreach (var c in panel.Children)
            {
                if (c is Control ctrl)
                    CollectTerminals(ctrl, result);
            }
        }
    }

}
