using Avalonia;
using Avalonia.Reactive;
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
            // Only the workspace is a frame; the list is a card like any other tile, with a header.
            Assert.Single(window.GetVisualDescendants().OfType<WindowTileFrame>());

            var listView = Assert.Single(window.GetVisualDescendants().OfType<WorkspacesPanelView>());
            Assert.Same(vm.WorkspacesPanel, listView.DataContext);
            var listCard = CardOf(listView);
            Assert.Equal(TileKindIds.Workspaces, Assert.IsType<LeafTileNodeViewModel>(listCard.DataContext).KindId);

            // Beside the layout, at the panel's width.
            Assert.Equal(WorkspacesTileKindWidth(settings), listCard.Bounds.Width, precision: 0);

            var layout = vm.WindowLayout!;
            var list = TileTreeEdits.LeavesOf(layout.RootTile).Single(tile => tile.KindId == TileKindIds.Workspaces);

            TileTreeEdits.ExecuteRootEdge(list, () => layout.RootTile, DropZone.Top,
                layout.FixedExtentFor(list, Orientation.Horizontal));
            window.UpdateLayout();

            // The same list control, now a strip along the top: its card at the strip's height.
            Assert.Same(listView, Assert.Single(window.GetVisualDescendants().OfType<WorkspacesPanelView>()));
            Assert.Equal(Services.Tiles.WorkspacesTileKind.StripHeight, CardOf(listView).Bounds.Height, precision: 0);

            vm.WindowLayout!.AddTile(TileKindIds.Note);
            window.UpdateLayout();

            var cards = window.GetVisualDescendants().OfType<LeafTileView>()
                .Select(card => card.DataContext).OfType<LeafTileNodeViewModel>()
                .Where(tile => TileTreeEdits.RootOf(tile) == layout.RootTile)
                .Select(tile => tile.KindId)
                .ToList();
            Assert.Equal(new[] { TileKindIds.Workspaces, TileKindIds.Note }.Order(), cards.Order());
        }
        finally
        {
            window.Close();
            vm.DisposeAll();
        }
    });

    /// <summary>The list is never handed the tile that stands for it as its data context, not even for a
    /// moment.</summary>
    /// <remarks>Its bindings are compiled against <c>WorkspacesPanelViewModel</c>. Put into its frame before
    /// being given one, it inherited the <c>LeafTileNodeViewModel</c> above it and its <c>FilterText</c>
    /// binding threw <c>InvalidCastException</c> on start-up. Built the way <c>App</c> builds the window —
    /// the data context in the initialiser, before <c>BindWindowState</c> — since that ordering is the one
    /// that produced it.</remarks>
    [Fact]
    public void The_list_never_inherits_its_tile_as_a_data_context() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));

        var vm = new MainWindowViewModel(workspaces, new PersistenceService(Path.Combine(_dir, "layouts")),
            settings, TestTiles.Catalog(settings),
            windowCatalog: panel => mTiles.App.BuildWindowTileCatalog(
                new AiUsageService(settings, sources: _ => []), panel),
            windowPersistence: new PersistenceService(Path.Combine(_dir, "window")));

        var seen = new List<Type?>();
        using var subscription = StyledElement.DataContextProperty.Changed.Subscribe(
            new AnonymousObserver<AvaloniaPropertyChangedEventArgs<object?>>(e =>
            {
                if (e.Sender is WorkspacesPanelView) seen.Add(e.NewValue.GetValueOrDefault()?.GetType());
            }));

        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.BindWindowState(settings);
        window.Show();
        window.UpdateLayout();

        try
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, type => Assert.True(type is null || type == typeof(WorkspacesPanelViewModel),
                $"The list was handed a {type?.Name} as its data context."));
            Assert.Same(vm.WorkspacesPanel,
                Assert.Single(window.GetVisualDescendants().OfType<WorkspacesPanelView>()).DataContext);
        }
        finally
        {
            window.Close();
            vm.DisposeAll();
        }
    });

    /// <summary>The card a control is drawn inside.</summary>
    private static LeafTileView CardOf(Visual view) =>
        view.GetVisualAncestors().OfType<LeafTileView>().First();

    private static double WorkspacesTileKindWidth(SettingsService settings) => settings.Settings.WorkspacesPanelWidth;
}
