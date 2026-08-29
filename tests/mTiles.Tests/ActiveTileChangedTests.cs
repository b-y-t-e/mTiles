using System.ComponentModel;
using Avalonia.Headless;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The signal that says "what a window-level listener is aimed at has moved" — another tile is active,
/// or the active one's own state changed.
/// </summary>
/// <remarks>
/// <para>Its first listener is the phone bridge, whose action list is documented as pushed rather than
/// polled (<c>docs/TILES.md</c>: <em>server → phone, when the active tile or its state changes</em>). With
/// nothing raising it, the list only ever moved when somebody dictated: a phone kept Git's buttons under
/// Git's name after the user had clicked into a Goal tile, and a Goal run that finished left Continue
/// greyed out on the phone — the page disables what its snapshot says is disabled, so the one thing the
/// feature exists for stopped working until the page was reloaded.</para>
/// <para>Both halves are tested, because they fail separately: the workspace raises it for its own tiles,
/// and the window follows whichever workspace is on screen.</para>
/// </remarks>
public class ActiveTileChangedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-active-tile").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    private WorkspaceViewModel BuildWorkspace(TempSettings settings) =>
        new(new Workspace { Name = "test", DirectoryPath = _dir }, settings.Layouts, settings.Service,
            TestTiles.Catalog(settings.Service));

    [Fact]
    public void Working_in_another_tile_is_a_change()
    {
        using var settings = new TempSettings();
        using var workspace = BuildWorkspace(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);

        // Split, so the second tile is one the workspace configured and can resolve as active — the
        // route a real one arrives by.
        root.SplitHorizontalCommand.Execute(null);
        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        var second = Assert.IsType<LeafTileNodeViewModel>(split.Second);

        var changes = 0;
        workspace.ActiveTileChanged += () => changes++;

        root.Activate();
        Assert.Equal(1, changes);
        Assert.Same(root, workspace.ActiveTile);

        second.Activate();
        Assert.Equal(2, changes);
        Assert.Same(second, workspace.ActiveTile);
    }

    /// <summary>
    /// The case the phone is for: nobody touched the tile, the run inside it finished, and what can be
    /// pressed is now different.
    /// </summary>
    [Fact]
    public void The_active_tile_changing_its_own_state_is_a_change()
    {
        using var settings = new TempSettings();
        using var workspace = BuildWorkspace(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        var content = new StubActions();
        root.Content = content;
        root.Activate();

        var changes = 0;
        workspace.ActiveTileChanged += () => changes++;

        content.Enable();
        Assert.Equal(1, changes);
    }

    /// <summary>A tile nobody is aimed at is nobody's business: it would be a broadcast per tick of every
    /// terminal in the workspace, saying nothing that had changed for the phone.</summary>
    [Fact]
    public void A_tile_that_is_not_the_active_one_changing_is_not()
    {
        using var settings = new TempSettings();
        using var workspace = BuildWorkspace(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.SplitHorizontalCommand.Execute(null);
        var split = Assert.IsType<SplitTileNodeViewModel>(workspace.RootTile);
        root.Activate();

        var background = Assert.IsType<LeafTileNodeViewModel>(split.Second);
        var content = new StubActions();
        background.Content = content;

        var changes = 0;
        workspace.ActiveTileChanged += () => changes++;

        content.Enable();
        Assert.Equal(0, changes);
    }

    /// <summary>"Nothing is active" is a state a listener has to be told about: it is the difference
    /// between a stale set of buttons and none.</summary>
    [Fact]
    public void Losing_the_active_tile_altogether_is_a_change()
    {
        using var settings = new TempSettings();
        using var workspace = BuildWorkspace(settings);

        var root = Assert.IsType<LeafTileNodeViewModel>(workspace.RootTile);
        root.Activate();

        var changes = 0;
        workspace.ActiveTileChanged += () => changes++;

        workspace.RootTile = new LeafTileNodeViewModel(TileKindIds.None, null, _dir,
            workspace.ActivationScope);

        Assert.Null(workspace.ActiveTile);
        Assert.Equal(1, changes);
    }

    /// <summary>The window follows whichever workspace is on screen, and lets go of the one that is
    /// not — or a listener would be told about tiles the user is no longer looking at.</summary>
    [Fact]
    public void The_window_follows_the_workspace_on_screen_and_only_that_one()
    {
        OnUiThread(() =>
        {
            var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
            workspaces.AddWorkspace(Path.Combine(_dir, "first"), "First");
            workspaces.AddWorkspace(Path.Combine(_dir, "second"), "Second");

            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            var window = new MainWindowViewModel(workspaces,
                new PersistenceService(Path.Combine(_dir, "layouts")), settings,
                TestTiles.Catalog(settings));

            var panel = window.WorkspacesPanel;
            panel.SelectedWorkspace = panel.Workspaces[0];
            var first = window.CurrentWorkspace!;

            var changes = 0;
            window.ActiveTileChanged += () => changes++;

            // Switching is itself a change: the active tile has become another workspace's.
            panel.SelectedWorkspace = panel.Workspaces[1];
            Assert.Equal(1, changes);

            // And the workspace left behind no longer speaks for the window.
            Assert.IsType<LeafTileNodeViewModel>(first.RootTile).Activate();
            Assert.Equal(1, changes);

            Assert.IsType<LeafTileNodeViewModel>(window.CurrentWorkspace!.RootTile).Activate();
            Assert.Equal(2, changes);

            window.DisposeAll();
        });
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ActiveTileChangedTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Tile content whose one action changes what it will allow, and says so the way every
    /// tile does — by raising a property change of its own.</summary>
    private sealed class StubActions : ITileActions
    {
        private bool _enabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string KindId => "stub";

        public IReadOnlyList<TileAction> Actions => [new("continue", "Continue", "play", _enabled)];

        public Task<TileActionResult> InvokeAsync(string id) => Task.FromResult(TileActionResult.Ok);

        public void Enable()
        {
            _enabled = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Actions)));
        }

        public void Dispose() { }
    }
}
