using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The workspace's tile tree is the surface's <c>Tree</c>, not a replacement for everything in it.
/// </summary>
/// <remarks>
/// <para><see cref="TileDropSurface"/> is a <c>Border</c>, and a <c>Border</c> already names a content
/// property of its own. Had the markup's child gone there, the build would still pass and the application
/// would still start: the tree would simply take the place of the surface's layers, the hint band would be
/// gone from the visual tree and every drop would resolve against a surface with no tree — a gesture that
/// silently does nothing, which is the one failure nothing else here would catch.</para>
/// </remarks>
public class TileDropSurfaceWiringTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileDropSurfaceWiringTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    [Fact]
    public void The_workspace_tree_is_laid_on_its_drop_surface() => OnUiThread(() =>
    {
        var view = new WorkspaceView();

        var surface = view.FindControl<TileDropSurface>("DropSurface");
        var tree = view.FindControl<TileNodeView>("RootTileView");

        Assert.NotNull(surface);
        Assert.NotNull(tree);
        Assert.Same(tree, surface.Tree);

        // The tree sits inside the surface's own layers, beside the hint, and not in their place.
        var layers = Assert.IsType<Grid>(surface.Child);
        Assert.Contains(tree, layers.Children);
        Assert.Equal(2, layers.Children.Count);
        Assert.True(DragDrop.GetAllowDrop(surface));
    });

    /// <summary>A control standing for one tile, drawing nothing.</summary>
    private sealed class FakeTarget(LeafTileNodeViewModel leaf) : Border, ITileDropTarget
    {
        public LeafTileNodeViewModel? DropNode => leaf;
        public void ShowDropOverlay(DropZone zone, string brushKey) { }
        public void HideDropOverlay() { }
    }

    private static LeafTileNodeViewModel Leaf() =>
        new(TileKindIds.None, null, Path.GetTempPath(), new TileActivationScope());

    private static SplitTileNodeViewModel Tree(out LeafTileNodeViewModel first, out LeafTileNodeViewModel second)
    {
        first = Leaf();
        second = Leaf();
        var split = new SplitTileNodeViewModel(Orientation.Vertical, first, second);
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    private static DropZone Centre(Control _) => DropZone.Center;

    private static void Nest(Panel parent, Control child) => parent.Children.Add(child);

    /// <summary>A tile of a tree nested inside a tile of this one is walked past, to the tile it sits in.</summary>
    [Fact]
    public void A_tile_of_a_nested_tree_is_walked_past_to_the_outer_tile() => OnUiThread(() =>
    {
        var outer = Tree(out var outerTarget, out var dragged);
        Tree(out var innerTarget, out _);

        var outerCard = new FakeTarget(outerTarget);
        var panel = new Panel();
        outerCard.Child = panel;
        var innerCard = new FakeTarget(innerTarget);
        Nest(panel, innerCard);

        var found = TileDropSurface.TargetUnder(innerCard, new Border(), outer, dragged, Centre);

        Assert.Equal(TileDropKind.Leaf, found.Kind);
        Assert.Same(outerCard, found.Target);
    });

    /// <summary>A gutter of a nested tree is walked past; one of this tree is taken.</summary>
    [Fact]
    public void Only_a_gutter_of_the_surface_tree_is_taken() => OnUiThread(() =>
    {
        var outer = Tree(out var outerTarget, out var dragged);
        var inner = Tree(out _, out _);

        var outerCard = new FakeTarget(outerTarget);
        var panel = new Panel();
        outerCard.Child = panel;
        var innerGutter = new GridSplitter { Classes = { "tile-gutter" }, DataContext = inner };
        Nest(panel, innerGutter);

        var pastInner = TileDropSurface.TargetUnder(innerGutter, new Border(), outer, dragged, Centre);
        Assert.Equal(TileDropKind.Leaf, pastInner.Kind);
        Assert.Same(outerCard, pastInner.Target);

        var ownOuter = Tree(out _, out _);
        var ownRoot = new SplitTileNodeViewModel(Orientation.Horizontal, ownOuter, Leaf());
        ownOuter.Parent = ownRoot;
        var ownGutter = new GridSplitter { Classes = { "tile-gutter" }, DataContext = ownOuter };
        var draggedFromElsewhere = Assert.IsType<LeafTileNodeViewModel>(ownRoot.Second);

        var taken = TileDropSurface.TargetUnder(ownGutter, new Border(), ownRoot, draggedFromElsewhere, Centre);
        Assert.Equal(TileDropKind.Gutter, taken.Kind);
        Assert.Same(ownOuter, taken.Split);
    });

    /// <summary>The walk stops at the surface that asked, so a tile drawn around it is never taken.</summary>
    [Fact]
    public void The_walk_stops_at_the_surface() => OnUiThread(() =>
    {
        var outer = Tree(out var outerTarget, out var dragged);

        var outerCard = new FakeTarget(outerTarget);
        var surface = new Panel();
        outerCard.Child = surface;
        var hit = new Border();
        Nest(surface, hit);

        var found = TileDropSurface.TargetUnder(hit, surface, outer, dragged, Centre);

        Assert.Equal(TileDropKind.None, found.Kind);
    });
}
