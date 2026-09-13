using Avalonia.Headless;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// One tile in the whole window is active — at whichever level it was last touched — and that is the
/// tile a dictated sentence, a shortcut and a phone's keys reach.
/// </summary>
/// <remarks>
/// <para>The window's layout and a workspace each keep an active tile of their own, which is what lets
/// each come back to the tile it left. Left at that, a note beside the workspaces could be the tile the
/// user is typing in while a terminal in the workspace still wore the outline and still received a
/// dictated sentence when the note's editor lost the keyboard — and with auto-Enter on, that sentence is
/// a command run in a terminal nobody was looking at.</para>
/// </remarks>
public class WindowActiveTileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public WindowActiveTileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowActiveTileTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private MainWindowViewModel Window(TempAppData appData)
    {
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
        workspaces.AddWorkspace(Path.Combine(_dir, "first"), "First");
        workspaces.AddWorkspace(Path.Combine(_dir, "second"), "Second");

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new MainWindowViewModel(workspaces, new PersistenceService(Path.Combine(_dir, "layouts")),
            settings, TestTiles.Catalog(settings),
            windowCatalog: panel => mTiles.App.BuildWindowTileCatalog(
                new AiUsageService(settings, sources: _ => []), panel),
            windowPersistence: new PersistenceService(Path.Combine(appData.Root, "window")));
    }

    [Fact]
    public void The_keyboard_is_at_whichever_level_was_last_touched() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var window = Window(appData);
        try
        {
            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[0];
            var terminal = Assert.IsType<LeafTileNodeViewModel>(window.CurrentWorkspace!.RootTile);
            terminal.Activate();
            Assert.Same(terminal, window.ActiveTile);

            window.WindowLayout!.AddTile(TileKindIds.Note);
            var note = TileTreeEdits.LeavesOf(window.WindowLayout!.RootTile).Single(t => t.KindId == TileKindIds.Note);

            var changes = 0;
            window.ActiveTileChanged += () => changes++;

            note.Activate();
            Assert.Same(note, window.ActiveTile);
            Assert.True(note.IsActive);
            // One outline in the window, not one per level.
            Assert.False(terminal.IsActive);
            Assert.True(changes > 0);

            terminal.Activate();
            Assert.Same(terminal, window.ActiveTile);
            Assert.True(terminal.IsActive);
            Assert.False(note.IsActive);
        }
        finally
        {
            window.DisposeAll();
        }
    });

    /// <summary>A window tile closed while it had the keyboard leaves nothing active, not the workspace.</summary>
    /// <remarks>The rule the workspace already follows for its own tiles: falling back to some other tile
    /// sends the next dictated sentence somewhere nobody chose.</remarks>
    [Fact]
    public void A_closed_window_tile_does_not_hand_the_keyboard_to_a_terminal() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var window = Window(appData);
        try
        {
            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[0];
            Assert.IsType<LeafTileNodeViewModel>(window.CurrentWorkspace!.RootTile).Activate();

            window.WindowLayout!.AddTile(TileKindIds.Note);
            var note = TileTreeEdits.LeavesOf(window.WindowLayout!.RootTile).Single(t => t.KindId == TileKindIds.Note);
            note.Activate();

            note.CloseCommand.Execute(null);

            Assert.Null(window.ActiveTile);
        }
        finally
        {
            window.DisposeAll();
        }
    });

    /// <summary>Choosing a workspace is choosing to work in it.</summary>
    [Fact]
    public void Switching_workspace_takes_the_keyboard_back_from_the_window() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var window = Window(appData);
        try
        {
            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[0];
            window.WindowLayout!.AddTile(TileKindIds.Note);
            var note = TileTreeEdits.LeavesOf(window.WindowLayout!.RootTile).Single(t => t.KindId == TileKindIds.Note);
            note.Activate();
            Assert.Same(note, window.ActiveTile);

            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[1];
            // Before any tile in it is touched: the note no longer speaks for the window.
            Assert.NotSame(note, window.ActiveTile);

            var second = Assert.IsType<LeafTileNodeViewModel>(window.CurrentWorkspace!.RootTile);
            second.Activate();

            Assert.Same(second, window.ActiveTile);
            Assert.False(note.IsActive);
        }
        finally
        {
            window.DisposeAll();
        }
    });

    /// <summary>A workspace saved while a window tile has the keyboard still names the tile it was left on.
    /// </summary>
    [Fact]
    public void A_workspace_saved_while_a_window_tile_has_the_keyboard_keeps_its_active_tile() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var window = Window(appData);
        try
        {
            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[0];
            var workspace = window.CurrentWorkspace!;
            var terminal = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
            terminal.Activate();

            window.WindowLayout!.AddTile(TileKindIds.Note);
            TileTreeEdits.LeavesOf(window.WindowLayout!.RootTile).Single(t => t.KindId == TileKindIds.Note).Activate();

            Assert.False(terminal.IsActive);
            Assert.Same(terminal, workspace.ActivationScope.LastActivated);
            Assert.True(workspace.ActiveTile == terminal);
        }
        finally
        {
            window.DisposeAll();
        }
    });

    /// <summary>Choosing a workspace takes the outline off the window tile, even before anything in the
    /// workspace is activated.</summary>
    [Fact]
    public void Switching_workspace_takes_the_outline_off_the_window_tile() => OnUiThread(() =>
    {
        using var appData = new TempAppData();
        var window = Window(appData);
        try
        {
            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[0];
            window.WindowLayout!.AddTile(TileKindIds.Note);
            var note = TileTreeEdits.LeavesOf(window.WindowLayout!.RootTile).Single(t => t.KindId == TileKindIds.Note);
            note.Activate();

            window.WorkspacesPanel.SelectedWorkspace = window.WorkspacesPanel.Workspaces[1];

            Assert.False(note.IsActive);
        }
        finally
        {
            window.DisposeAll();
        }
    });
}
