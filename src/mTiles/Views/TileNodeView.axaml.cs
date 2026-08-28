using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using mTiles.Services;
using mTiles.ViewModels;

namespace mTiles.Views;

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

        var scope = FindActivationScope();
        var guard = scope?.SuppressActivation();
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
            guard?.Dispose();
            _isBuilding = false;
        }

        _owner?.RefreshMinimums();
    }

    private TileActivationScope? FindActivationScope()
    {
        return _vm switch
        {
            LeafTileNodeViewModel leaf => leaf.ActivationScope,
            SplitTileNodeViewModel => FindLeafScope(_vm),
            _ => null
        };
    }

    private static TileActivationScope? FindLeafScope(TileNodeViewModel? node)
    {
        while (node is SplitTileNodeViewModel split)
            node = split.First;
        return (node as LeafTileNodeViewModel)?.ActivationScope;
    }

    private void ShowLeaf(LeafTileNodeViewModel leaf)
    {
        _firstChild = null;
        _secondChild = null;

        if (Content is LeafTileView existing && existing.DataContext == leaf)
            return;

        Content = new LeafTileView { DataContext = leaf };
    }

    /// <summary>
    /// How much canvas shows between two tiles.
    /// <para>Wide enough to read as a gap rather than a seam — the tiles are cards on
    /// <c>BgCanvas</c>, and at the old three pixels the rounded corners of two neighbours touched and
    /// the gap looked like a rendering fault. It is also the splitter's whole hit area, so this is the
    /// grab handle's width as much as it is the gutter's.</para>
    /// </summary>
    private const int TileGap = 8;

    /// <summary>The view of the split this one sits in, if there is one.</summary>
    /// <remarks>Splitting a tile deep inside a pane raises what every pane above it may be shrunk to,
    /// and only the node holding that tile is told about the change — so the news travels back up the
    /// way the views were built.</remarks>
    private TileNodeView? _owner;

    private void ShowSplit(SplitTileNodeViewModel split)
    {
        _firstChild ??= new TileNodeView { _owner = this };
        _secondChild ??= new TileNodeView { _owner = this };

        ControlHelper.DetachFromParent(_firstChild);
        ControlHelper.DetachFromParent(_secondChild);

        if (_firstChild.DataContext != split.First)
            _firstChild.DataContext = split.First;
        if (_secondChild.DataContext != split.Second)
            _secondChild.DataContext = split.Second;

        var grid = new Grid();
        var splitter = new GridSplitter
        {
            Classes = { "tile-gutter" },
            ResizeDirection = split.Orientation == Orientation.Vertical
                ? GridResizeDirection.Columns
                : GridResizeDirection.Rows
        };
        splitter.DragCompleted += (_, _) => UpdateSplitRatio(split, grid);
        // The minimums depend on how much room this split has, so they are taken again whenever that
        // changes — a narrowed window or a dragged panel splitter, neither of which rebuilds anything.
        grid.SizeChanged += (_, _) => ApplyMinimums(split, grid);

        if (split.Orientation == Orientation.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(split.SplitRatio, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(TileGap, GridUnitType.Pixel)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - split.SplitRatio, GridUnitType.Star)));

            Grid.SetColumn(_firstChild, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_secondChild, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(split.SplitRatio, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(TileGap, GridUnitType.Pixel)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1 - split.SplitRatio, GridUnitType.Star)));

            Grid.SetRow(_firstChild, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(_secondChild, 2);
        }

        grid.Children.Add(_firstChild);
        grid.Children.Add(splitter);
        grid.Children.Add(_secondChild);

        ApplyMinimums(split, grid);

        Content = grid;
    }

    /// <summary>
    /// Stops the splitter dragging either side below what the tiles on that side need
    /// (<see cref="TileMinimumSize"/>).
    /// </summary>
    /// <remarks>
    /// <para>The minimum belongs on the pane's own definition because that is what the splitter
    /// clamps against, but it is asked of the whole subtree: a star-sized pane never grows to what its
    /// content needs, so a pane holding a further split would otherwise lay its two tiles out beyond
    /// its own edge and under the card next door.</para>
    /// <para>And it is asked for no more than there is (<see cref="TileMinimumSize.Fit"/>): the window
    /// has no minimum size of its own and the workspaces panel has its own splitter, so the content
    /// column can be narrower than the tiles in it want. Two floors adding up to more than the grid has
    /// would then spill the far tile past the edge instead of shrinking anything — the disappearing
    /// tile again, reached by narrowing the window rather than by dragging a tile splitter.</para>
    /// </remarks>
    private static void ApplyMinimums(SplitTileNodeViewModel split, Grid grid)
    {
        if (split.Orientation == Orientation.Vertical && grid.ColumnDefinitions.Count >= 3)
        {
            var (first, second) = TileMinimumSize.Fit(
                TileMinimumSize.Width(split.First, TileGap),
                TileMinimumSize.Width(split.Second, TileGap),
                grid.Bounds.Width - TileGap);

            grid.ColumnDefinitions[0].MinWidth = first;
            grid.ColumnDefinitions[2].MinWidth = second;
        }
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
        {
            var (first, second) = TileMinimumSize.Fit(
                TileMinimumSize.Height(split.First, TileGap),
                TileMinimumSize.Height(split.Second, TileGap),
                grid.Bounds.Height - TileGap);

            grid.RowDefinitions[0].MinHeight = first;
            grid.RowDefinitions[2].MinHeight = second;
        }
    }

    /// <summary>Takes the minimums again, here and in every split this one sits in.</summary>
    private void RefreshMinimums()
    {
        if (_vm is SplitTileNodeViewModel split && Content is Grid grid)
            ApplyMinimums(split, grid);

        _owner?.RefreshMinimums();
    }

    /// <summary>
    /// Stores where the splitter came to rest, as the star weights it left behind.
    /// <para>The weights and not the measured sizes: the splitter writes the weights synchronously as
    /// it is dragged — already clamped by the minimums — while <c>ActualWidth</c> is one layout pass
    /// behind at <c>DragCompleted</c>, so reading it saves the split from before the drag.</para>
    /// </summary>
    private static void UpdateSplitRatio(SplitTileNodeViewModel split, Grid grid)
    {
        if (split.Orientation == Orientation.Vertical && grid.ColumnDefinitions.Count >= 3)
            StoreRatio(split, grid.ColumnDefinitions[0].Width.Value, grid.ColumnDefinitions[2].Width.Value);
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
            StoreRatio(split, grid.RowDefinitions[0].Height.Value, grid.RowDefinitions[2].Height.Value);
    }

    private static void StoreRatio(SplitTileNodeViewModel split, double first, double second)
    {
        var total = first + second;
        if (total > 0)
            split.SplitRatio = first / total;
    }
}
