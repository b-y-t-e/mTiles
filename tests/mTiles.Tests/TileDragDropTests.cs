using Avalonia;
using Avalonia.Layout;
using mTiles.Models;
using System.Linq;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Dropping one tile onto the middle of another.
/// </summary>
/// <remarks>
/// <para>The gesture reads as "these two change places", and that is now literally what happens: the
/// two leaves exchange their slots in the tree. It used to trade their contents instead, which looks
/// the same and is not — a terminal reads <c>${tileId}</c> through the function its
/// <c>TileContext</c> was built with, and that function answers with the id of the leaf that created
/// it. Content moved and the closure could not, so both terminals ended up reading the other tile's id
/// and "Restart shell" relaunched each of them under its neighbour's session — a Claude Code
/// conversation, or an OpenCode one, opened in the wrong tile.</para>
/// <para>Which is why the assertion here is not "the tiles moved" but "each terminal still answers with
/// the id its own tile is saved under": that pairing is the thing the swap has broken before.</para>
/// </remarks>
public class TileDragDropTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public TileDragDropTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private WorkspaceViewModel Build(TempSettings settings) =>
        new(new Workspace { Name = "test", DirectoryPath = _directory }, settings.Layouts,
            settings.Service, TestTiles.Catalog(settings.Service));

    [Fact]
    public void Swapping_two_tiles_leaves_every_terminal_reading_its_own_tile_id()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);

        var contentOfFirst = Assert.IsType<TerminalTileViewModel>(first.Content);
        var contentOfSecond = Assert.IsType<TerminalTileViewModel>(second.Content);
        var (idOfFirst, idOfSecond) = (first.TileId, second.TileId);
        Assert.NotEqual(idOfFirst, idOfSecond);

        TileDragDrop.Execute(first, second, DropZone.Center);

        // The tiles changed places...
        Assert.Same(second, split.First);
        Assert.Same(first, split.Second);

        // ...and each of them took its content and its identity along, unchanged.
        Assert.Same(contentOfFirst, first.Content);
        Assert.Same(contentOfSecond, second.Content);
        Assert.Equal(idOfFirst, first.TileId);
        Assert.Equal(idOfSecond, second.TileId);
        Assert.Equal(idOfFirst, contentOfFirst.TileId);
        Assert.Equal(idOfSecond, contentOfSecond.TileId);
    }

    /// <summary>A "New session" on a swapped tile still reaches the terminal that tile is holding.</summary>
    /// <remarks>The half of the pairing the other test cannot see: reading the right id once could also
    /// be a value copied at the right moment, and this is what tells the two apart.</remarks>
    [Fact]
    public void A_new_id_after_a_swap_reaches_the_terminal_that_tile_holds()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (_, first, second) = TwoTerminals(workspace);
        var contentOfFirst = Assert.IsType<TerminalTileViewModel>(first.Content);

        TileDragDrop.Execute(first, second, DropZone.Center);

        first.TileId = "fresh-session";

        Assert.Equal("fresh-session", contentOfFirst.TileId);
    }

    /// <summary>The gutter of a split takes a tile between the two it holds.</summary>
    /// <remarks>The tree is binary, so "three side by side" is a split inside a split. What says the
    /// gesture worked is therefore not the shape but the three shares: a third each, with the pair that
    /// was already there keeping its proportion. A nested split left at its own default ratio renders
    /// as one wide tile beside two narrow ones, which is the failure this asserts against.</remarks>
    [Fact]
    public void A_tile_dropped_on_a_gutter_goes_between_the_two_tiles_it_holds()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);
        second.SplitVerticalCommand.Execute(null);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        var third = Assert.IsType<LeafTileNodeViewModel>(inner.Second);

        TileDragDrop.ExecuteGutter(third, split);

        Assert.Same(first, split.First);
        var pair = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        Assert.Same(third, pair.First);
        Assert.Same(second, pair.Second);
        Assert.Same(split, pair.Parent);
        Assert.Same(pair, third.Parent);

        Assert.Equal(1.0 / 3, split.SplitRatio, precision: 9);
        Assert.Equal(1.0 / 3, (1 - split.SplitRatio) * pair.SplitRatio, precision: 9);
    }

    /// <summary>The hint for a gutter drop is drawn against the room the split will have after the
    /// detach, not the room it has now.</summary>
    /// <remarks>With the dragged tile beside the split rather than inside it, taking it out lifts the
    /// split into the whole workspace — a hint sized from today's bounds showed half of what the tile
    /// actually got.</remarks>
    [Fact]
    public void A_split_beside_the_dragged_tile_is_measured_after_the_tile_has_left()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);
        second.SplitVerticalCommand.Execute(null);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);

        Assert.Same(inner, TileDragDrop.LiftedByDetach(first, inner));

        var workspaceBounds = new Rect(0, 0, 900, 600);
        var innerBounds = new Rect(450, 0, 450, 600);
        Assert.Equal(workspaceBounds, TileDragDrop.AfterDetach(innerBounds, innerBounds, workspaceBounds));
    }

    /// <summary>A rectangle inside the lifted node is moved and scaled with it.</summary>
    /// <remarks>The case above moves the whole lifted node, where the answer is the vacated bounds
    /// however the offset is computed. Half of the node is what tells a correct translation from one
    /// that only happens to agree at the origin: the lifted node here starts at x=450, so an offset
    /// taken from the wrong corner, or scaled before it is subtracted, lands somewhere else.</remarks>
    [Fact]
    public void A_rectangle_inside_the_lifted_node_moves_and_scales_with_it()
    {
        var vacated = new Rect(0, 0, 900, 600);
        var lifted = new Rect(450, 0, 450, 600);
        var rightHalf = new Rect(675, 0, 225, 600);

        Assert.Equal(new Rect(450, 0, 450, 600), TileDragDrop.AfterDetach(rightHalf, lifted, vacated));

        var lowerHalfOfStack = new Rect(450, 300, 450, 300);
        Assert.Equal(new Rect(0, 300, 900, 300), TileDragDrop.AfterDetach(lowerHalfOfStack, lifted, vacated));
    }

    /// <summary>A split the dragged tile is not beside keeps its bounds through the detach.</summary>
    [Fact]
    public void A_split_the_detach_does_not_lift_is_measured_as_it_stands()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, _, second) = TwoTerminals(workspace);
        second.SplitVerticalCommand.Execute(null);
        var inner = Assert.IsType<SplitTileNodeViewModel>(split.Second);
        var third = Assert.IsType<LeafTileNodeViewModel>(inner.Second);

        Assert.Null(TileDragDrop.LiftedByDetach(third, split));
    }

    /// <summary>A tile dropped on the gutter of its own split asks for what is already on screen.</summary>
    /// <remarks>And the reason it is refused rather than allowed to be a no-op: detaching it lifts its
    /// sibling into the split's slot, which takes the split out of the tree — so the insert would go
    /// into a node nobody draws, and the tile would be gone.</remarks>
    [Fact]
    public void A_tile_dropped_on_its_own_gutter_is_left_where_it_is()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);

        TileDragDrop.ExecuteGutter(second, split);

        Assert.Same(first, split.First);
        Assert.Same(second, split.Second);
        Assert.Same(split, workspace.RootTile);
    }

    /// <summary>The workspace's edge gives a tile a column beside the whole layout.</summary>
    [Fact]
    public void A_tile_dropped_on_the_workspace_edge_becomes_a_column_beside_everything()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);
        second.SplitVerticalCommand.Execute(null);
        var third = Assert.IsType<LeafTileNodeViewModel>(
            Assert.IsType<SplitTileNodeViewModel>(split.Second).Second);

        TileDragDrop.ExecuteWorkspaceEdge(third, () => workspace.RootTile, DropZone.Left);

        var root = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        Assert.Equal(Orientation.Vertical, root.Orientation);
        Assert.Same(third, root.First);
        Assert.Equal(1.0 / 3, root.SplitRatio, precision: 9);

        // The layout it was lifted out of came along whole, and the dropped tile is no longer in it.
        var rest = Assert.IsType<SplitTileNodeViewModel>(root.Second);
        Assert.Same(first, rest.First);
        Assert.Same(second, rest.Second);
        Assert.Same(root, third.Parent);
    }

    /// <summary>
    /// The root is read again after the detach, because the detach can replace it.
    /// </summary>
    /// <remarks>With two tiles in the workspace, taking one out lifts the other into the root's own
    /// slot — so a root captured before the detach is a split that is no longer in the tree, and the
    /// new root would be built around it with the surviving tile inside twice.</remarks>
    [Fact]
    public void The_edge_drop_builds_on_the_root_the_detach_left_behind()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var (split, first, second) = TwoTerminals(workspace);

        TileDragDrop.ExecuteWorkspaceEdge(second, () => workspace.RootTile, DropZone.Bottom);

        var root = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        Assert.NotSame(split, root);
        Assert.Equal(Orientation.Horizontal, root.Orientation);
        Assert.Same(first, root.First);
        Assert.Same(second, root.Second);
        Assert.Equal(2.0 / 3, root.SplitRatio, precision: 9);
    }

    /// <summary>A workspace of one tile has nothing to put a column beside.</summary>
    [Fact]
    public void The_workspace_edge_refuses_a_workspace_of_one_tile()
    {
        using var settings = new TempSettings();
        using var workspace = Build(settings);

        var only = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        MakeTerminal(only);

        TileDragDrop.ExecuteWorkspaceEdge(only, () => workspace.RootTile, DropZone.Right);

        Assert.Same(only, workspace.RootTile);
    }

    /// <summary>Which edge of the workspace a pointer is in the band of.</summary>
    /// <remarks>
    /// <para>A table rather than a <c>Theory</c> because <c>DropZone</c> is internal to the view layer
    /// and an xUnit 2 theory's parameters have to be as public as the method holding them. Written out
    /// here so the rule is still read as a table.</para>
    /// <para>The negative coordinates are not a curiosity: the workspace's padding is drawn outside the
    /// tile tree, and the drop target is the whole workspace, so a pointer in that padding is as much
    /// on that edge as one a pixel inside is.</para>
    /// </remarks>
    [Fact]
    public void The_workspace_band_is_the_outer_edge_and_the_padding_beyond_it()
    {
        var workspace = new Size(400, 400);

        (double X, double Y, DropZone Expected)[] cases =
        [
            (4, 200, DropZone.Left),
            (396, 200, DropZone.Right),
            (200, 4, DropZone.Top),
            (200, 396, DropZone.Bottom),

            // Past the band, where the tile underneath answers instead.
            (200, 200, DropZone.None),
            (60, 200, DropZone.None),

            // In the workspace's padding, outside the tile tree entirely.
            (-6, 200, DropZone.Left),
            (406, 200, DropZone.Right),
            (200, -6, DropZone.Top),
        ];

        foreach (var (x, y, expected) in cases)
            Assert.Equal(expected, TileDragDrop.GetWorkspaceEdge(new Point(x, y), workspace));
    }

    /// <summary>A workspace narrower than two bands still has a middle.</summary>
    [Fact]
    public void The_band_never_takes_more_than_a_third_of_the_shorter_side()
    {
        var tiny = new Size(60, 60);

        Assert.Equal(DropZone.Left, TileDragDrop.GetWorkspaceEdge(new Point(4, 30), tiny));
        Assert.Equal(DropZone.None, TileDragDrop.GetWorkspaceEdge(new Point(30, 30), tiny));
    }

    /// <summary>A workspace of two terminals side by side, and the split holding them.</summary>
    private static (SplitTileNodeViewModel Split, LeafTileNodeViewModel First, LeafTileNodeViewModel Second)
        TwoTerminals(WorkspaceViewModel workspace)
    {
        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        MakeTerminal(root);
        root.SplitVerticalCommand.Execute(null);

        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        var first = Assert.IsType<LeafTileNodeViewModel>(split.First);
        var second = Assert.IsType<LeafTileNodeViewModel>(split.Second);
        MakeTerminal(second);

        return (split, first, second);
    }

    /// <summary>Gives an empty tile a terminal on the default shell.</summary>
    /// <remarks>Through the chooser, because that is the route a user takes: whether a step comes first
    /// depends on what profiles this machine's settings hold, and the default shell is the one option
    /// that carries no state.</remarks>
    private static void MakeTerminal(LeafTileNodeViewModel leaf)
    {
        leaf.SelectKindCommand.Execute(TileKindIds.Terminal);
        if (leaf.IsChoosingSetup)
            leaf.SelectSetupOptionCommand.Execute(leaf.SetupOptions.First(o => o.State is null));
    }
}
