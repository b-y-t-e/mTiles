using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The window drawn through its own tile layout: the list and the workspace in frames, everything else
/// in cards, and the list moved rather than rebuilt when the layout changes.
/// </summary>
/// <remarks>
/// <para>What a screenshot can say and a model test cannot: that the list which is on screen after it
/// has been moved is the same control that was on screen before. Built again, it would come back without
/// its scroll position and its filter, and the cache of workspace views beside it — which is what keeps
/// every workspace's shells alive — would be rebuilt with it.</para>
/// </remarks>
public class WindowLayoutViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public WindowLayoutViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowLayoutViewTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    [Fact]
    public void The_list_is_moved_not_rebuilt_and_a_window_tile_is_a_card() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));

        var vm = new MainWindowViewModel(workspaces, new PersistenceService(Path.Combine(_dir, "layouts")),
            settings, TestTiles.Catalog(settings),
            windowCatalog: panel => mTiles.App.BuildWindowTileCatalog(
                new AiUsageService(settings, sources: _ => []), panel),
            windowPersistence: new PersistenceService(Path.Combine(_dir, "window")));

        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.BindWindowState(settings);
        window.Show();
        window.UpdateLayout();

        try
        {
            var frames = window.GetVisualDescendants().OfType<WindowTileFrame>().ToList();
            Assert.Equal(2, frames.Count);

            var listView = Assert.Single(window.GetVisualDescendants().OfType<WorkspacesPanelView>());
            Assert.Same(vm.WorkspacesPanel, listView.DataContext);

            // Beside the layout, at the panel's width.
            Assert.Equal(WorkspacesTileKindWidth(settings), listView.Bounds.Width, precision: 0);

            var layout = vm.WindowLayout!;
            var list = TileTreeEdits.LeavesOf(layout.RootTile).Single(tile => tile.KindId == TileKindIds.Workspaces);

            TileTreeEdits.ExecuteRootEdge(list, () => layout.RootTile, DropZone.Top,
                layout.FixedExtentFor(list, Orientation.Horizontal));
            window.UpdateLayout();

            // The same control, now a strip along the top.
            Assert.Same(listView, Assert.Single(window.GetVisualDescendants().OfType<WorkspacesPanelView>()));
            Assert.Equal(Services.Tiles.WorkspacesTileKind.StripHeight, listView.Bounds.Height, precision: 0);

            vm.AddWindowTileCommand.Execute(TileKindIds.Note);
            window.UpdateLayout();

            var cards = window.GetVisualDescendants().OfType<LeafTileView>()
                .Select(card => card.DataContext).OfType<LeafTileNodeViewModel>()
                .Where(tile => TileTreeEdits.RootOf(tile) == layout.RootTile)
                .ToList();
            Assert.Equal(TileKindIds.Note, Assert.Single(cards).KindId);
        }
        finally
        {
            window.Close();
            vm.DisposeAll();
        }
    });

    private static double WorkspacesTileKindWidth(SettingsService settings) => settings.Settings.WorkspacesPanelWidth;
}
