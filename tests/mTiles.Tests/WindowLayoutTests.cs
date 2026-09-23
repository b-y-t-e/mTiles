using Avalonia;
using Avalonia.Headless;
using Avalonia.Layout;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The window's own layout: one list of workspaces, one place the open workspace is drawn, and whatever
/// has been put beside them.
/// </summary>
/// <remarks>
/// <para>The first thing asserted is the thing nobody should be able to notice: a window that has never
/// been rearranged looks as it did before it had a layout, and writes nothing down. After that, what
/// the two permanent tiles refuse, what a file that lost one of them comes back as, and what the list's
/// fixed size is on each axis.</para>
/// <para>On the UI thread, because the list's own view model starts the timer that reads branch names.
/// </para>
/// </remarks>
public class WindowLayoutTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string WindowDir => Path.Combine(_dir.Path, "window");
    private string LayoutFile => Path.Combine(WindowDir, WindowLayoutViewModel.LayoutId + ".json");

    /// <summary>Everything a window layout needs, built against this test's own directory.</summary>
    private sealed class Fixture : IDisposable
    {
        public TempSettings Settings { get; }
        public WorkspacesPanelViewModel Panel { get; }
        public TileCatalog Catalog { get; }
        public PersistenceService Persistence { get; }
        private readonly string _windowDir;

        public Fixture(string dir, string windowDir)
        {
            Settings = new TempSettings(Path.Combine(dir, "settings"));
            Panel = new WorkspacesPanelViewModel(Settings.Workspaces, Settings.Service);
            Catalog = TestTiles.WindowCatalog(Settings.Service, Panel);
            Persistence = new PersistenceService(windowDir);
            _windowDir = windowDir;
        }

        public WindowLayoutViewModel Layout(double listWidth = WorkspacesTileKind.DefaultWidth) =>
            new(Persistence, Settings.Service, Catalog, _windowDir, listWidth);

        public void Dispose()
        {
            Panel.Dispose();
            Settings.Dispose();
        }
    }

    // ---- the default -------------------------------------------------------------------------------

    [Fact]
    public void A_window_never_rearranged_is_the_list_on_the_left_and_the_workspace_beside_it() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout(listWidth: 310);

        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.Equal(Orientation.Vertical, root.Orientation);

        var list = Assert.IsType<LeafTileNodeViewModel>(root.First);
        var host = Assert.IsType<LeafTileNodeViewModel>(root.Second);
        Assert.Equal(TileKindIds.Workspaces, list.KindId);
        Assert.Equal(TileKindIds.WorkspaceHost, host.KindId);

        // At the width the panel had, held in pixels, with the workspace taking the rest.
        Assert.Equal(SplitFixedSide.First, root.FixedSide);
        Assert.Equal(310, root.FixedExtent);

        Assert.Same(fixture.Panel, Assert.IsType<WorkspacesTileViewModel>(list.Content).Panel);
    });

    /// <summary>Opening the window writes nothing: a layout nobody has changed is not a change.</summary>
    [Fact]
    public void The_default_layout_is_not_written_down() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();

        // A save that had been scheduled would be written now.
        fixture.Persistence.FlushLayout(WindowLayoutViewModel.LayoutId);

        Assert.False(File.Exists(LayoutFile));
    });

    // ---- persistence -------------------------------------------------------------------------------

    [Fact]
    public void A_rearranged_window_comes_back_as_it_was_left() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);

        using (var layout = fixture.Layout())
        {
            Assert.NotNull(layout.AddTile(TileKindIds.Note));
        }

        // Disposing the layout flushes the pending save.
        Assert.True(File.Exists(LayoutFile));

        using var reopened = fixture.Layout();

        var root = Assert.IsType<SplitTileNodeViewModel>(reopened.RootTile);
        var inner = Assert.IsType<SplitTileNodeViewModel>(root.First);
        Assert.Equal(TileKindIds.Workspaces, Assert.IsType<LeafTileNodeViewModel>(inner.First).KindId);
        Assert.Equal(SplitFixedSide.First, inner.FixedSide);
        Assert.Equal(TileKindIds.Note, Assert.IsType<LeafTileNodeViewModel>(root.Second).KindId);
    });

    /// <summary>A file that lost the list, or holds two, is replaced rather than mended.</summary>
    /// <remarks>Mending it would mean guessing where a list nobody put anywhere should go. The default is
    /// the one layout that is certainly usable, and the notes that file named stay on disk.</remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_file_without_exactly_one_list_opens_as_the_default(int lists) => Ui.Run(() =>
    {
        Directory.CreateDirectory(WindowDir);
        var leaves = Enumerable.Range(0, lists)
            .Select(_ => new TileNode { IsLeaf = true, Kind = TileKindIds.Workspaces, TileId = Guid.NewGuid().ToString() })
            .Append(new TileNode { IsLeaf = true, Kind = TileKindIds.WorkspaceHost, TileId = Guid.NewGuid().ToString() })
            .ToList();
        var root = leaves.Skip(1).Aggregate(leaves[0], (first, second) =>
            new TileNode { IsLeaf = false, First = first, Second = second });

        using var fixture = new Fixture(_dir.Path, WindowDir);
        fixture.Persistence.SaveLayout(WindowLayoutViewModel.LayoutId, root);

        using var layout = fixture.Layout();

        var split = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.Equal(TileKindIds.Workspaces, Assert.IsType<LeafTileNodeViewModel>(split.First).KindId);
        Assert.Equal(TileKindIds.WorkspaceHost, Assert.IsType<LeafTileNodeViewModel>(split.Second).KindId);
    });

    // ---- what the permanent tiles refuse -----------------------------------------------------------

    [Fact]
    public void The_list_and_the_workspace_cannot_be_closed_or_changed() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();
        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);

        foreach (var tile in new[] { root.First, root.Second }.Cast<LeafTileNodeViewModel>())
        {
            Assert.False(tile.CanClose);

            tile.CloseCommand.Execute(null);
            Assert.Same(root, layout.RootTile);

            tile.RefreshChangeKindOptions();
            Assert.False(tile.CanChangeKind);
        }
    });

    /// <summary>A tile beside them can become anything the window allows — except one of them.</summary>
    [Fact]
    public void Nothing_offers_a_second_list_or_a_second_workspace() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();

        Assert.Null(layout.AddTile(TileKindIds.Workspaces));
        Assert.Null(layout.AddTile(TileKindIds.WorkspaceHost));

        var note = Assert.IsType<LeafTileNodeViewModel>(layout.AddTile(TileKindIds.Note));
        Assert.True(note.CanClose);

        note.RefreshChangeKindOptions();
        Assert.DoesNotContain(note.ChangeKindOptions, choice => choice.Label is "Workspaces" or "Workspace");
        Assert.DoesNotContain(note.AvailableKinds, kind => kind.IsPermanent);
    });

    /// <summary>A note beside the workspaces keeps its file in the window's directory.</summary>
    [Fact]
    public void A_window_note_keeps_its_file_in_the_window_directory() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();

        var note = Assert.IsType<LeafTileNodeViewModel>(layout.AddTile(TileKindIds.Note));

        var path = Assert.IsType<NoteTileViewModel>(note.Content).FilePath;
        Assert.StartsWith(WindowDir, path);
    });

    // ---- the list's size ---------------------------------------------------------------------------

    /// <summary>Beside the layout the list is as wide as it last was; along it, one strip of tabs — at any
    /// window size.</summary>
    [Fact]
    public void The_list_keeps_its_width_through_a_trip_to_the_top() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();
        // A window too small for a note's pixels: the list keeps its own size all the same.
        layout.Size = new Size(500, 700);
        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        var list = Assert.IsType<LeafTileNodeViewModel>(root.First);
        var host = Assert.IsType<LeafTileNodeViewModel>(root.Second);
        _ = layout.AddTile(TileKindIds.Note);

        // The user widens the list by dragging its splitter.
        root.FixedExtent = 300;

        TileTreeEdits.ExecuteRootEdge(list, () => layout.RootTile, DropZone.Top,
            layout.DropSizeFor(list, Orientation.Horizontal));

        var top = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.Equal(Orientation.Horizontal, top.Orientation);
        Assert.Same(list, top.First);
        Assert.Equal(WorkspacesTileKind.StripHeight, top.FixedExtent);

        TileTreeEdits.ExecuteRootEdge(list, () => layout.RootTile, DropZone.Left,
            layout.DropSizeFor(list, Orientation.Vertical));

        var left = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.Same(list, left.First);
        Assert.Equal(SplitFixedSide.First, left.FixedSide);
        Assert.Equal(300, left.FixedExtent);

        // The workspace gives no size of its own on either route.
        Assert.Null(layout.FixedExtentFor(host, Orientation.Vertical));
        Assert.Null(layout.DropSizeFor(host, Orientation.Vertical));
    });

    [Fact]
    public void Swapping_the_list_with_the_workspace_keeps_the_width_on_the_list() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();
        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        var list = Assert.IsType<LeafTileNodeViewModel>(root.First);
        var host = Assert.IsType<LeafTileNodeViewModel>(root.Second);

        TileTreeEdits.Execute(list, host, DropZone.Center);

        Assert.Same(host, root.First);
        Assert.Same(list, root.Second);
        Assert.True(root.IsFixed(list));
        Assert.Equal(WorkspacesTileKind.DefaultWidth, root.FixedExtent);
    });

    [Fact]
    public void A_permanent_kind_spelled_in_other_letters_still_counts() => Ui.Run(() =>
    {
        Directory.CreateDirectory(WindowDir);
        var root = new TileNode
        {
            IsLeaf = false,
            First = new TileNode { IsLeaf = true, Kind = "Workspaces", TileId = Guid.NewGuid().ToString() },
            Second = new TileNode
            {
                IsLeaf = false,
                First = new TileNode { IsLeaf = true, Kind = TileKindIds.WorkspaceHost, TileId = Guid.NewGuid().ToString() },
                Second = new TileNode { IsLeaf = true, Kind = TileKindIds.Note, TileId = Guid.NewGuid().ToString() }
            }
        };

        using var fixture = new Fixture(_dir.Path, WindowDir);
        fixture.Persistence.SaveLayout(WindowLayoutViewModel.LayoutId, root);

        using var layout = fixture.Layout();

        var split = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        Assert.IsType<SplitTileNodeViewModel>(split.Second);
    });

    /// <summary>Splitting the list beside the layout puts the new tile beside its column, not inside it.
    /// </summary>
    /// <remarks>The list is held at a size in pixels chosen for a list of names. Split in place, the new
    /// tile would be squeezed into those pixels with it — so it goes where a drop on that edge puts a tile,
    /// between the list and the workspace, taking its third from the workspace's side. Split downwards, it
    /// stays in the column: that axis is not the fixed one.</remarks>
    [Fact]
    public void Splitting_the_list_sideways_puts_the_new_tile_beside_its_column() => Ui.Run(() =>
    {
        using var fixture = new Fixture(_dir.Path, WindowDir);
        using var layout = fixture.Layout();
        layout.Size = new Size(1600, 900);
        var root = Assert.IsType<SplitTileNodeViewModel>(layout.RootTile);
        var list = Assert.IsType<LeafTileNodeViewModel>(root.First);
        var host = Assert.IsType<LeafTileNodeViewModel>(root.Second);

        list.SplitVerticalCommand.Execute(null);

        Assert.Same(root, layout.RootTile);
        Assert.Same(list, root.First);
        Assert.True(root.IsFixed(list));
        Assert.Equal(WorkspacesTileKind.DefaultWidth, root.FixedExtent);

        var beside = Assert.IsType<SplitTileNodeViewModel>(root.Second);
        var newcomer = Assert.IsType<LeafTileNodeViewModel>(beside.First);
        Assert.Equal(TileKindIds.None, newcomer.KindId);
        Assert.Same(host, beside.Second);
        // On a wide window, a narrow column of fixed width.
        Assert.True(beside.IsFixed(newcomer));
        Assert.Equal(WindowTileSize.Width, beside.FixedExtent);

        // The window holds only what needs no repository, and offers nothing permanent.
        Assert.Equal(
            new[] { TileKindIds.Note, TileKindIds.Todo, TileKindIds.Usage }.Order(),
            newcomer.AvailableKinds.Select(kind => kind.Id).Order());

        list.SplitHorizontalCommand.Execute(null);
        var column = Assert.IsType<SplitTileNodeViewModel>(root.First);
        Assert.Equal(Orientation.Horizontal, column.Orientation);
        Assert.Same(list, column.First);
    });
}
