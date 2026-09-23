using Avalonia;
using Avalonia.Headless;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A note, a todo list or a usage dashboard put beside the workspaces is narrow: fixed pixels while the
/// window has room for them twice over, a share of the room when it has not.
/// </summary>
/// <remarks>These tiles sit beside the workspaces rather than in place of them, and half of whatever they
/// were dropped on — the share a tile inside a workspace gets — took half the window away from the thing
/// the window is for.</remarks>
public class WindowTileSizeTests
{
    // ---- the rule ------------------------------------------------------------------------------------

    [Fact]
    public void Pixels_while_the_window_has_room_for_them_twice_over_a_share_otherwise()
    {
        (Orientation Axis, double Width, double Height, double? Pixels, double? Share)[] cases =
        [
            // A wide window: a column of fixed width beside the layout.
            (Orientation.Vertical, 1600, 900, WindowTileSize.Width, null),
            // Exactly twice is not more than twice: a share.
            (Orientation.Vertical, 2 * WindowTileSize.Width, 900, null, WindowTileSize.Share),
            // A narrow window: the pixels would be most of it.
            (Orientation.Vertical, 500, 900, null, WindowTileSize.Share),
            // Along the top or the bottom it is the height that counts, not the width.
            (Orientation.Horizontal, 500, 900, WindowTileSize.Height, null),
            (Orientation.Horizontal, 1600, 400, null, WindowTileSize.Share),
            // Nothing measured yet is too small for pixels.
            (Orientation.Vertical, 0, 0, null, WindowTileSize.Share),
        ];

        foreach (var (axis, width, height, pixels, share) in cases)
        {
            var size = WindowTileSize.For(axis, new Size(width, height));
            Assert.Equal(pixels, size.Pixels);
            Assert.Equal(share, size.Share);
        }
    }

    // ---- drops and splits in the window --------------------------------------------------------------

    [Fact]
    public void A_note_dropped_beside_everything_on_a_wide_window_is_a_fixed_column() => Ui.Run(() =>
    {
        using var fixture = new Fixture();
        using var layout = fixture.Layout();
        layout.Size = new Size(1600, 900);
        var note = Assert.IsType<LeafTileNodeViewModel>(layout.AddTile(TileKindIds.Note));

        TileTreeEdits.ExecuteRootEdge(note, () => layout.RootTile, DropZone.Right,
            layout.DropSizeFor(note, Orientation.Vertical));

        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.Same(note, root.Second);
        Assert.True(root.IsFixed(note));
        Assert.Equal(WindowTileSize.Width, root.FixedExtent);
    });

    [Fact]
    public void A_note_dropped_on_a_small_window_takes_a_share_and_scales_with_it() => Ui.Run(() =>
    {
        using var fixture = new Fixture();
        using var layout = fixture.Layout();
        layout.Size = new Size(500, 700);
        var note = Assert.IsType<LeafTileNodeViewModel>(layout.AddTile(TileKindIds.Note));
        var host = TileTreeEdits.LeavesOf(layout.RootTile).Single(t => t.KindId == TileKindIds.WorkspaceHost);

        TileTreeEdits.Execute(note, host, DropZone.Left, layout.DropSizeFor(note, Orientation.Vertical));

        var split = Assert.IsType<SplitTileNodeViewModel>(note.Parent);
        Assert.Same(note, split.First);
        Assert.Same(host, split.Second);
        Assert.Equal(SplitFixedSide.None, split.FixedSide);
        Assert.Equal(WindowTileSize.Share, split.SplitRatio, precision: 9);
    });

    /// <summary>Splitting a window tile gives the new empty tile the same narrow room a drop would.</summary>
    [Fact]
    public void A_tile_split_off_a_window_tile_is_narrow_too() => Ui.Run(() =>
    {
        using var fixture = new Fixture();
        using var layout = fixture.Layout();
        var note = Assert.IsType<LeafTileNodeViewModel>(layout.AddTile(TileKindIds.Note));

        layout.Size = new Size(1600, 900);
        note.SplitVerticalCommand.Execute(null);
        var wide = Assert.IsType<SplitTileNodeViewModel>(note.Parent);
        Assert.Equal(SplitFixedSide.Second, wide.FixedSide);
        Assert.Equal(WindowTileSize.Width, wide.FixedExtent);

        layout.Size = new Size(1600, 300);
        note.SplitHorizontalCommand.Execute(null);
        var short_ = Assert.IsType<SplitTileNodeViewModel>(note.Parent);
        Assert.Equal(Orientation.Horizontal, short_.Orientation);
        Assert.Equal(SplitFixedSide.None, short_.FixedSide);
        Assert.Equal(1 - WindowTileSize.Share, short_.SplitRatio, precision: 9);
    });

    // ---- the hints -----------------------------------------------------------------------------------

    [Fact]
    public void The_edge_hint_is_the_share_when_the_drop_is_given_one()
    {
        var area = new Size(1000, 600);

        RectAssert.Close(new Rect(0, 0, 300, 600),
            TileDropGeometry.EdgeBand(area, DropZone.Left, TileDropSize.AsShare(0.3), 8, 50));
        RectAssert.Close(new Rect(700, 0, 300, 600),
            TileDropGeometry.EdgeBand(area, DropZone.Right, TileDropSize.AsShare(0.3), 8, 50));
    }

    /// <summary>A sized gutter drop is that size, against the gutter, in the side it goes into.</summary>
    [Fact]
    public void The_gutter_hint_is_the_size_against_the_gutter()
    {
        var room = new Rect(0, 0, 1008, 600);
        var split = new SplitTileNodeViewModel(Orientation.Vertical,
            new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope()),
            new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope()));

        // An ordinary split at a half: the second side starts after the gutter.
        RectAssert.Close(new Rect(512, 0, 320, 600),
            TileDropGeometry.GutterBand(room, split, 8, TileDropSize.InPixels(320)));

        // Beside a fixed list of 240: the newcomer starts after the list and the gutter.
        split.Fix(SplitFixedSide.First, 240);
        RectAssert.Close(new Rect(248, 0, 320, 600),
            TileDropGeometry.GutterBand(room, split, 8, TileDropSize.InPixels(320)));
        RectAssert.Close(new Rect(248, 0, 0.3 * 760, 600),
            TileDropGeometry.GutterBand(room, split, 8, TileDropSize.AsShare(0.3)));
    }

    /// <summary>Pixels wider than the side leaves are drawn at the cap the layout puts on them.</summary>
    [Fact]
    public void The_gutter_hint_in_a_narrow_side_is_capped_as_the_layout_caps_it()
    {
        var room = new Rect(0, 0, 758, 600);
        var split = new SplitTileNodeViewModel(Orientation.Vertical,
            new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope()),
            new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope()));
        split.Fix(SplitFixedSide.First, 400);

        // A side of 350: the newcomer keeps a gutter and the neighbour's 50 px out of it.
        RectAssert.Close(new Rect(408, 0, 292, 600),
            TileDropGeometry.GutterBand(room, split, 8, TileDropSize.InPixels(320)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly string _windowDir;

        public Fixture()
        {
            _windowDir = _dir["window"];
            Settings = new TempSettings(_dir["settings"]);
            Panel = new WorkspacesPanelViewModel(Settings.Workspaces, Settings.Service);
            Persistence = new PersistenceService(_windowDir);
        }

        public TempSettings Settings { get; }
        public WorkspacesPanelViewModel Panel { get; }
        public PersistenceService Persistence { get; }

        public WindowLayoutViewModel Layout() =>
            new(Persistence, Settings.Service, TestTiles.WindowCatalog(Settings.Service, Panel), _windowDir);

        public void Dispose()
        {
            Panel.Dispose();
            Settings.Dispose();
            _dir.Dispose();
        }
    }
}
