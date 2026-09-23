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
    private readonly TempDirectory _dir = new("mtiles-unload");

    private MainWindowViewModel NewWindow() => TestMainWindow.Create(_dir.Path);

    /// <summary>A workspace nobody has opened holds nothing, and its row has to say so: the shade is the
    /// only thing on the row that tells the two apart.</summary>
    [Fact]
    public void A_row_is_loaded_from_the_moment_its_workspace_is_opened()
    {
        Ui.Run(() =>
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
        Ui.Run(() =>
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
        Ui.Run(() =>
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
        Ui.Run(() =>
        {
            var panel = NewWindow().WorkspacesPanel;
            var unopened = panel.Workspaces.Single(w => w != panel.SelectedWorkspace);

            Assert.False(panel.UnloadWorkspaceCommand.CanExecute(unopened));
            Assert.True(panel.UnloadWorkspaceCommand.CanExecute(panel.SelectedWorkspace));
        });
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Unload asks its own question, not the update's.</summary>
    /// <remarks>
    /// One callback served both, and the window wired it with the update's title and button — so
    /// pressing Unload raised a dialog headed <i>Update Available</i> with an <i>Update</i> button, over
    /// the unload text. It unloaded the workspace when pressed, so nothing was lost but the user's
    /// confidence in what the button does; a wrong heading on a destructive question is exactly where
    /// somebody presses the wrong thing.
    /// </remarks>
    [Fact]
    public void Unloading_does_not_borrow_the_update_dialog()
    {
        Ui.Run(() =>
        {
            var vm = NewWindow();
            var panel = vm.WorkspacesPanel;

            var askedToUnload = new List<string>();
            var askedToUpdate = new List<string>();
            vm.ConfirmAction = m => { askedToUnload.Add(m); return Task.FromResult(true); };
            vm.ConfirmUpdateAction = m => { askedToUpdate.Add(m); return Task.FromResult(true); };

            panel.UnloadWorkspaceCommand.Execute(panel.SelectedWorkspace!);

            Assert.Contains(askedToUnload, m => m.Contains("Unload workspace"));
            Assert.Empty(askedToUpdate);
        });
    }
}
