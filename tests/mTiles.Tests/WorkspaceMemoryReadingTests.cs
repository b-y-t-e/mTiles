using System.ComponentModel;
using Avalonia.Headless;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The reading on a workspace row, from the tiles' processes to the words.
/// </summary>
/// <remarks>
/// The pure halves are argued elsewhere (<see cref="ProcessTreeMemoryTests"/>, <c>MemoryDisplay</c>);
/// what is only here is the join this class alone makes — the ids of a workspace's tiles grouped under
/// that workspace's own id, and a row that is not loaded left out of both the question and the answer.
/// Getting that wrong shows up as one workspace wearing another's figure, which is the kind of wrong a
/// number never announces.
/// </remarks>
public class WorkspaceMemoryReadingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-memory").FullName;

    /// <summary>A probe that measures nothing and remembers everything it was asked.</summary>
    private sealed class FakeProbe : IProcessMemoryProbe
    {
        public required Func<IReadOnlyCollection<int>, long> Reading { get; init; }
        public List<IReadOnlyDictionary<string, IReadOnlyCollection<int>>> Questions { get; } = [];

        public IReadOnlyDictionary<string, long> WorkingSetsOf(
            IReadOnlyDictionary<string, IReadOnlyCollection<int>> rootProcessIdsByGroup)
        {
            Questions.Add(rootProcessIdsByGroup);
            return rootProcessIdsByGroup.ToDictionary(group => group.Key, group => Reading(group.Value));
        }
    }

    /// <summary>Tile content that runs a process and does nothing else.</summary>
    private sealed class ProcessTileStub : IProcessTile
    {
        public ProcessTileStub(int? childProcessId) => ChildProcessId = childProcessId;
        public string KindId => "note";
        public int? ChildProcessId { get; }
        // Nothing here ever changes, so there is nothing to notify about.
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspaceMemoryReadingTests).Assembly);
        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private MainWindowViewModel NewWindow(IProcessMemoryProbe probe)
    {
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
        workspaces.AddWorkspace(Path.Combine(_dir, "first"), "First");
        workspaces.AddWorkspace(Path.Combine(_dir, "second"), "Second");

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new MainWindowViewModel(
            workspaces,
            new PersistenceService(Path.Combine(_dir, "layouts")),
            settings,
            TestTiles.Catalog(settings),
            memoryProbe: probe);
    }

    private static void PutProcessInTile(WorkspaceViewModel workspace, int? processId) =>
        ((LeafTileNodeViewModel)workspace.RootTile!).Content = new ProcessTileStub(processId);

    private const long ThreeHundredMegabytes = 300L * 1024 * 1024;

    /// <summary>The whole path: a tile's process id is asked about under its own workspace's id, and the
    /// answer comes back as the words on that workspace's row.</summary>
    [Fact]
    public void A_tile_s_process_is_asked_about_under_its_own_workspace_and_lands_on_that_row()
    {
        OnUiThread(async () =>
        {
            var probe = new FakeProbe { Reading = roots => roots.Contains(4242) ? ThreeHundredMegabytes : 0 };
            var vm = NewWindow(probe);
            var panel = vm.WorkspacesPanel;

            var withProcess = panel.SelectedWorkspace!;
            PutProcessInTile(vm.CurrentWorkspace!, 4242);

            var without = panel.Workspaces.Single(w => w != withProcess);
            panel.SelectedWorkspace = without;
            PutProcessInTile(vm.CurrentWorkspace!, null);

            await vm.SampleMemoryAsync();

            // One reading for both workspaces, each keyed by its own id.
            var question = Assert.Single(probe.Questions);
            Assert.Equal(new[] { 4242 }, question[withProcess.Id]);
            Assert.Empty(question[without.Id]);

            Assert.Equal(MemoryDisplay.Format(ThreeHundredMegabytes), withProcess.MemoryText);
            Assert.Equal("", without.MemoryText);
        });
    }

    /// <summary>A workspace nobody has opened is not asked about: it holds no tiles, so there is nothing
    /// of it in the process table and a row for it would be a figure invented out of an empty set.</summary>
    [Fact]
    public void An_unopened_workspace_is_not_asked_about()
    {
        OnUiThread(async () =>
        {
            var probe = new FakeProbe { Reading = _ => ThreeHundredMegabytes };
            var vm = NewWindow(probe);
            var panel = vm.WorkspacesPanel;
            var unopened = panel.Workspaces.Single(w => w != panel.SelectedWorkspace);

            await vm.SampleMemoryAsync();

            Assert.DoesNotContain(unopened.Id, Assert.Single(probe.Questions).Keys);
            Assert.Equal("", unopened.MemoryText);
        });
    }

    /// <summary>Unloading is the answer to a figure the user did not like, so a sample started before it
    /// must not write the figure back afterwards and undo the row.</summary>
    [Fact]
    public void A_reading_for_a_workspace_unloaded_meanwhile_is_dropped()
    {
        OnUiThread(async () =>
        {
            var probe = new FakeProbe { Reading = _ => ThreeHundredMegabytes };
            var vm = NewWindow(probe);
            var panel = vm.WorkspacesPanel;
            vm.ConfirmAction = _ => Task.FromResult(true);

            var row = panel.SelectedWorkspace!;
            var sample = vm.SampleMemoryAsync();
            panel.UnloadWorkspaceCommand.Execute(row);
            await sample;

            Assert.False(row.IsLoaded);
            Assert.Equal("", row.MemoryText);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
