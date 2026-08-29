using Avalonia.Headless;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Removing a workspace lets go of it — the view model and the subscription that drives its row.
/// </summary>
/// <remarks>
/// <para>The removal path is a collection-changed handler that has to tell a Remove from a Move: a Move
/// carries the item it moved in <c>OldItems</c> too, so treating "anything with OldItems" as a removal
/// disposed the tiles of a workspace the user had merely dragged up the list. Nothing exercised that
/// distinction, and the two cases differ by one enum member.</para>
/// <para>The panel's "working" light is the other half: a workspace's view model raises it, so a
/// removed one that is still subscribed keeps writing to a row that belongs to nobody.</para>
/// </remarks>
public class WorkspaceRemovalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-removal").FullName;

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspaceRemovalTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// A window over two workspaces that already exist — the panel reads the service when it is built,
    /// so adding them afterwards would leave it looking at an empty list.
    /// </summary>
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

    [Fact]
    public void A_removed_workspace_is_let_go_of_and_a_reordered_one_is_not()
    {
        OnUiThread(() =>
        {
            var vm = NewWindow();
            var vmPanel = vm.WorkspacesPanel;
            Assert.Equal(2, vmPanel.Workspaces.Count);

            var removed = new List<string>();
            vm.WorkspaceRemoved += id => removed.Add(id);

            // Opening them is what puts a view model in the cache — there is nothing to let go of
            // before that.
            foreach (var open in vmPanel.Workspaces.ToList())
                vmPanel.SelectedWorkspace = open;

            // A Move: the rows are re-ordered and both workspaces are still open.
            var row = vmPanel.Workspaces[0];
            vmPanel.Workspaces.Move(0, 1);
            Assert.Empty(removed);

            // A Remove: this one is gone for good.
            vmPanel.Workspaces.Remove(row);
            Assert.Equal([row.Id], removed);

            vm.DisposeAll();
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }
}
