using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using mTiles.Models;
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
        if (_vm is not SplitTileNodeViewModel split) return;

        if (e.PropertyName is nameof(SplitTileNodeViewModel.First)
                or nameof(SplitTileNodeViewModel.Second)
                or nameof(SplitTileNodeViewModel.Orientation)
                or nameof(SplitTileNodeViewModel.Solo)
                or nameof(SplitTileNodeViewModel.FixedSide))
        {
            Rebuild();
        }
        else if (e.PropertyName is nameof(SplitTileNodeViewModel.FixedExtent))
        {
            // In place rather than by a rebuild: the splitter itself writes this when it comes to rest,
            // and replacing the grid from inside that splitter's own DragCompleted would tear down the
            // control whose event is still being delivered. When it was the splitter, the definition
            // already holds the value and nothing is written.
            ApplyLengths(split, Content as Grid);
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
    internal const int TileGap = 8;

    /// <summary>The view of the split this one sits in, if there is one.</summary>
    /// <remarks>Splitting a tile deep inside a pane raises what every pane above it may be shrunk to,
    /// and only the node holding that tile is told about the change — so the news travels back up the
    /// way the views were built.</remarks>
    private TileNodeView? _owner;

    /// <summary>The child view holding this split's soloed child, or null when nothing is soloed here.</summary>
    private TileNodeView? SoloView(SplitTileNodeViewModel split)
    {
        if (split.Solo is not { } solo) return null;
        if (ReferenceEquals(solo, split.First)) return _firstChild;
        if (ReferenceEquals(solo, split.Second)) return _secondChild;
        return null;
    }

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

        // A maximized tile is shown by the splits above it drawing one child and nothing else — no
        // grid, no splitter, no gutter. The child views are the ones this split has always held, so the
        // tile that fills the workspace is the same control it was in the layout: its terminal is not
        // rebuilt, its scrollback is not lost, and putting the layout back is this method running again
        // with Solo cleared. The other side is detached from the visual tree, which does not end a
        // session — only its UI timers pause.
        // Asked as "which of the two is it", never as "is it the first, otherwise the second": a Solo
        // left behind by a tile that has since been re-parented — the other side of this split closed,
        // so the survivor was lifted into the grandparent — is a child of neither, and answering
        // "second" for it draws a branch nobody maximized while the maximized tile disappears along
        // with the header that could bring it back. A stale Solo simply draws the split.
        if (SoloView(split) is { } soloView)
        {
            ControlHelper.DetachFromParent(_firstChild);
            ControlHelper.DetachFromParent(_secondChild);
            Content = soloView;
            return;
        }

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

        var (firstLength, secondLength) = LengthsFor(split);

        if (split.Orientation == Orientation.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(firstLength));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(TileGap, GridUnitType.Pixel)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(secondLength));

            Grid.SetColumn(_firstChild, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_secondChild, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(firstLength));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(TileGap, GridUnitType.Pixel)));
            grid.RowDefinitions.Add(new RowDefinition(secondLength));

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
            var available = grid.Bounds.Width - TileGap;
            var (first, second) = TileMinimumSize.Fit(
                TileMinimumSize.Width(split.First, TileGap),
                TileMinimumSize.Width(split.Second, TileGap),
                available);

            grid.ColumnDefinitions[0].MinWidth = first;
            grid.ColumnDefinitions[2].MinWidth = second;
            grid.ColumnDefinitions[0].MaxWidth = MaximumFor(split, SplitFixedSide.First, first, second, available);
            grid.ColumnDefinitions[2].MaxWidth = MaximumFor(split, SplitFixedSide.Second, second, first, available);
        }
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
        {
            var available = grid.Bounds.Height - TileGap;
            var (first, second) = TileMinimumSize.Fit(
                TileMinimumSize.Height(split.First, TileGap),
                TileMinimumSize.Height(split.Second, TileGap),
                available);

            grid.RowDefinitions[0].MinHeight = first;
            grid.RowDefinitions[2].MinHeight = second;
            grid.RowDefinitions[0].MaxHeight = MaximumFor(split, SplitFixedSide.First, first, second, available);
            grid.RowDefinitions[2].MaxHeight = MaximumFor(split, SplitFixedSide.Second, second, first, available);
        }
    }

    /// <summary>The cap on one pane's length: only a fixed side has one (<see cref="TileMinimumSize.FixedMaximum"/>).</summary>
    /// <remarks>A star pane is already held inside the split by the grid itself; a pixel pane is not, and
    /// is what would push the pane beside it past the edge.</remarks>
    private static double MaximumFor(
        SplitTileNodeViewModel split, SplitFixedSide side, double ownMinimum, double otherMinimum, double available) =>
        split.FixedSide == side
            ? TileMinimumSize.FixedMaximum(ownMinimum, otherMinimum, available)
            : double.PositiveInfinity;

    /// <summary>Takes the minimums again, here and in every split this one sits in.</summary>
    private void RefreshMinimums()
    {
        if (_vm is SplitTileNodeViewModel split && Content is Grid grid)
            ApplyMinimums(split, grid);

        _owner?.RefreshMinimums();
    }

    /// <summary>The lengths a split's two panes are laid out at.</summary>
    /// <remarks>
    /// <para>Shares of what the split has, as star weights, unless a side is fixed: then that side is its
    /// pixels and the other is a single star, which is what lets it take whatever the window leaves.</para>
    /// <para>A fixed side's pixels are not capped here but on its definition's maximum
    /// (<see cref="ApplyMinimums"/>), which is taken again whenever the grid changes size: a grid gives a
    /// pixel length its pixels before a star gets anything, so a minimum on the other pane alone would
    /// not stop it being pushed past the edge — and a cap computed here, at build time, would be one
    /// measured before the first layout pass, which is no size at all.</para>
    /// </remarks>
    internal static (GridLength First, GridLength Second) LengthsFor(SplitTileNodeViewModel split) =>
        split.FixedSide switch
        {
            SplitFixedSide.First => (new GridLength(split.FixedExtent, GridUnitType.Pixel), new GridLength(1, GridUnitType.Star)),
            SplitFixedSide.Second => (new GridLength(1, GridUnitType.Star), new GridLength(split.FixedExtent, GridUnitType.Pixel)),
            _ => (new GridLength(split.SplitRatio, GridUnitType.Star), new GridLength(1 - split.SplitRatio, GridUnitType.Star))
        };

    /// <summary>Puts the split's lengths back on a grid already built for it, where they differ.</summary>
    private static void ApplyLengths(SplitTileNodeViewModel split, Grid? grid)
    {
        if (grid is null) return;
        var (first, second) = LengthsFor(split);

        if (split.Orientation == Orientation.Vertical && grid.ColumnDefinitions.Count >= 3)
        {
            if (grid.ColumnDefinitions[0].Width != first) grid.ColumnDefinitions[0].Width = first;
            if (grid.ColumnDefinitions[2].Width != second) grid.ColumnDefinitions[2].Width = second;
        }
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
        {
            if (grid.RowDefinitions[0].Height != first) grid.RowDefinitions[0].Height = first;
            if (grid.RowDefinitions[2].Height != second) grid.RowDefinitions[2].Height = second;
        }
    }

    /// <summary>
    /// Stores where the splitter came to rest, as the lengths it left behind.
    /// <para>The lengths and not the measured sizes: the splitter writes them synchronously as it is
    /// dragged — already clamped by the minimums — while <c>ActualWidth</c> is one layout pass behind at
    /// <c>DragCompleted</c>, so reading it saves the split from before the drag.</para>
    /// </summary>
    private static void UpdateSplitRatio(SplitTileNodeViewModel split, Grid grid)
    {
        if (split.Orientation == Orientation.Vertical && grid.ColumnDefinitions.Count >= 3)
            StoreRest(split, grid.ColumnDefinitions[0].Width, grid.ColumnDefinitions[2].Width);
        else if (split.Orientation == Orientation.Horizontal && grid.RowDefinitions.Count >= 3)
            StoreRest(split, grid.RowDefinitions[0].Height, grid.RowDefinitions[2].Height);
    }

    /// <summary>Writes the splitter's resting place back into the split.</summary>
    /// <remarks>A fixed side keeps being a size in pixels after it is dragged: the splitter resizes a
    /// pixel definition in pixels and leaves the star beside it a star, so the fixed side's new length is
    /// the whole answer and the ratio is left as it was.</remarks>
    internal static void StoreRest(SplitTileNodeViewModel split, GridLength first, GridLength second)
    {
        switch (split.FixedSide)
        {
            case SplitFixedSide.First when first.IsAbsolute:
                split.FixedExtent = first.Value;
                return;
            case SplitFixedSide.Second when second.IsAbsolute:
                split.FixedExtent = second.Value;
                return;
            case SplitFixedSide.None:
                var total = first.Value + second.Value;
                if (total > 0)
                    split.SplitRatio = first.Value / total;
                return;
        }
    }
}
