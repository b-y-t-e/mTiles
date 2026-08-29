using Avalonia.Headless;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Loaded and unloaded, as the panel row says it and as the context menu changes it.
/// </summary>
/// <remarks>
/// A workspace holds its tiles — and their shells — from the first time it is opened until the window
/// closes, which is what makes a day's work end with six agents resident. Unloading is the way back, and
/// it is destructive in the way Restart shell is: it stops whatever those shells are running. The row's
/// own state is the other half, because an unload that leaves a row still saying "loaded, 400 MB" reads
/// as an unload that did not happen.
/// </remarks>
public class WorkspaceUnloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-unload").FullName;

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspaceUnloadTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private MainWindowViewModel NewWindow()
    {
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
        workspaces.AddWorkspace(Path.Combine(_dir, "first"), "First");
        workspaces.AddWorkspace(Path.Combine(_dir, "second"), "Second");

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new MainWindowViewModel(
            workspaces,
            new PersistenceService(Path.Combine(_dir, "layouts")),
            settings,
            TestTiles.Catalog(settings));
    }

    /// <summary>A workspace nobody has opened holds nothing, and its row has to say so: the shade is the
    /// only thing on the row that tells the two apart.</summary>
    [Fact]
    public void A_row_is_loaded_from_the_moment_its_workspace_is_opened()
    {
        OnUiThread(() =>
        {
            var vm = NewWindow();
            var panel = vm.WorkspacesPanel;

            var unopened = panel.Workspaces.Single(w => w != panel.SelectedWorkspace);
            Assert.False(unopened.IsLoaded);
            Assert.True(panel.SelectedWorkspace!.IsLoaded);

            panel.SelectedWorkspace = unopened;
            Assert.True(unopened.IsLoaded);
        });
    }

    /// <summary>The whole gesture: the tiles go, the row goes back to how an unopened one looks, and the
    /// selection is let go of so that clicking the row again builds the workspace afresh.</summary>
    [Fact]
    public void Unloading_lets_go_of_the_tiles_and_of_the_row()
    {
        OnUiThread(() =>
        {
            var vm = NewWindow();
            var panel = vm.WorkspacesPanel;
            vm.ConfirmAction = _ => Task.FromResult(true);

            var released = new List<string>();
            vm.WorkspaceRemoved += id => released.Add(id);

            var row = panel.SelectedWorkspace!;
            row.MemoryText = "312 MB";

            panel.UnloadWorkspaceCommand.Execute(row);

            Assert.Equal([row.Id], released);
            Assert.False(row.IsLoaded);
            Assert.Equal("", row.MemoryText);
            Assert.Null(vm.CurrentWorkspace);
            Assert.Null(panel.SelectedWorkspace);

            // And the way back is the same click that opened it the first time.
            panel.SelectWorkspaceCommand.Execute(row);
            Assert.True(row.IsLoaded);
            Assert.NotNull(vm.CurrentWorkspace);
        });
    }

    /// <summary>An unwired confirmation answers no. The shells being closed are running somebody's work,
    /// and a question nobody was asked is not a yes.</summary>
    [Fact]
    public void An_unanswered_confirmation_keeps_the_workspace()
    {
        OnUiThread(() =>
        {
            var vm = NewWindow();
            var panel = vm.WorkspacesPanel;

            var row = panel.SelectedWorkspace!;
            panel.UnloadWorkspaceCommand.Execute(row);

            Assert.True(row.IsLoaded);
            Assert.NotNull(vm.CurrentWorkspace);
        });
    }

    /// <summary>Dead on a row with nothing to give back, rather than quietly doing nothing: the menu
    /// item is the only place this is offered, so it has to say when it is not on offer.</summary>
    [Fact]
    public void The_menu_item_is_dead_for_a_workspace_that_was_never_opened()
    {
        OnUiThread(() =>
        {
            var panel = NewWindow().WorkspacesPanel;
            var unopened = panel.Workspaces.Single(w => w != panel.SelectedWorkspace);

            Assert.False(panel.UnloadWorkspaceCommand.CanExecute(unopened));
            Assert.True(panel.UnloadWorkspaceCommand.CanExecute(panel.SelectedWorkspace));
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
