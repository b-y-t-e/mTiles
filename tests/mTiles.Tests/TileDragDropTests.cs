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
