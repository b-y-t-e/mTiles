using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A workspace that has just been added is brought into view.
/// </summary>
/// <remarks>
/// <para>Selecting a row highlights it and nothing more: the list is an <c>ItemsControl</c>, not a
/// <c>ListBox</c>, so no scroller moves on the view model's behalf. The row also lands wherever the
/// display order puts it — pinned rows first, then alphabetically — so "it was just added" says nothing
/// about where it is, and in a list longer than the panel the one row the user is certainly looking for
/// was opened and highlighted off screen.</para>
/// <para>Rendered rather than asserted on a flag, because the part that can break is the timing: the
/// row is asked for in the same breath as it is added, and its container does not exist until the layout
/// pass that follows. A test that only checked the callback had fired would pass with
/// <c>ContainerFromItem</c> answering null.</para>
/// </remarks>
public class WorkspaceRevealTests : IDisposable
{
    private readonly TempDirectory _dir = new("mtiles-reveal");

    public void Dispose() => _dir.Dispose();

    private static void Pump()
    {
        // Layout, then the Loaded-priority job the reveal is posted at, then whatever the scroll itself
        // queues. Three passes rather than one: each of those is a separate turn of the loop.
        for (var i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void A_workspace_added_below_the_fold_is_scrolled_to()
        => Ui.Run(async () =>
        {
            var workspaces = new WorkspaceService(Path.Combine(_dir.Path, "workspaces.json"));
            // Enough rows that the last one cannot be on screen in a 260px panel, and named so the one
            // added below sorts to the end rather than into the middle.
            for (var i = 0; i < 40; i++)
                workspaces.AddWorkspace(Path.Combine(_dir.Path, $"ws{i:00}"), $"Workspace {i:00}");

            var settings = new SettingsService(Path.Combine(_dir.Path, "settings.json"));
            var panel = new WorkspacesPanelViewModel(workspaces, settings);

            ControlThemes.EnsureFluent();

            var view = new WorkspacesPanelView { DataContext = panel, Width = 240 };
            var window = new Window { Content = view, Width = 240, Height = 260 };
            window.Show();
            Pump();

            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.GetVisualDescendants().OfType<ItemsControl>().Any(c => c.Name == "WorkspaceList"));
            Assert.Equal(0, scroller.Offset.Y);

            var added = Path.Combine(_dir.Path, "zz-last");
            panel.FolderPicker = () => Task.FromResult<string?>(added);
            await panel.AddWorkspaceCommand.ExecuteAsync(null);
            Pump();

            var item = Assert.Single(panel.Workspaces, w => w.DirectoryPath == added);
            Assert.Same(item, panel.SelectedWorkspace);
            Assert.Same(item, panel.Workspaces[^1]);

            var list = view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "WorkspaceList");
            var container = list.ContainerFromItem(item);
            Assert.NotNull(container);

            // The list scrolled, and far enough that the new row is inside the viewport rather than
            // merely nearer to it.
            Assert.True(scroller.Offset.Y > 0,
                $"the list did not scroll: offset {scroller.Offset.Y}, viewport {scroller.Viewport}, " +
                $"the new row at {container!.Bounds}");

            var top = Avalonia.VisualExtensions.TranslatePoint(container!, default, scroller)!.Value.Y;
            Assert.InRange(top, 0, scroller.Viewport.Height);
        });

    /// <summary>The workspace the last session left open is scrolled to when the panel appears.</summary>
    /// <remarks>It is selected in <c>MainWindowViewModel</c>'s constructor, before this view exists, so
    /// nothing ever asked for it: the application opened with the row it had just restored — and its
    /// highlight, the only thing that says which workspace is open — below the fold of a list longer
    /// than the panel.</remarks>
    [Fact]
    public void The_restored_workspace_is_scrolled_to_when_the_panel_opens()
        => Ui.Run(() =>
        {
            var (panel, _) = APanelOfForty();

            // What MainWindowViewModel does with AppSettings.LastWorkspaceId, before any view exists.
            var restored = panel.Workspaces[^1];
            panel.SelectedWorkspace = restored;

            ControlThemes.EnsureFluent();
            var (view, scroller) = Shown(panel);

            AssertInView(view, scroller, restored);
            return Task.CompletedTask;
        });

    /// <summary>Pinning a row follows it to where the order has just put it.</summary>
    /// <remarks>Pinned rows sort to the top and unpinning drops one back into the alphabet, so the
    /// gesture moves the row out from under the pointer — in a long list, to somewhere the user then
    /// has to go and find. Driven from the bottom of the list, because a row pinned while the list is
    /// already at the top is on screen either way and proves nothing.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pinning_a_row_scrolls_to_where_it_moved(bool startsPinned)
        => Ui.Run(() =>
        {
            var (panel, _) = APanelOfForty();
            var row = panel.Workspaces[^1];
            if (startsPinned)
            {
                panel.ToggleFavoriteCommand.Execute(row);
                Assert.Same(row, panel.Workspaces[0]);
            }

            ControlThemes.EnsureFluent();
            var (view, scroller) = Shown(panel);

            // Parked at the far end from where the row is about to land: pinning sends it to the top,
            // so the list starts at the bottom, and unpinning drops it back into the alphabet at the
            // end, so the list starts at the top. Parked at the same end instead, the row would be on
            // screen after the move whether anything scrolled or not, and the test would pass with the
            // reveal deleted — which is exactly what it did.
            scroller.Offset = startsPinned
                ? new Avalonia.Vector(0, 0)
                : new Avalonia.Vector(0, scroller.Extent.Height);
            Pump();

            panel.ToggleFavoriteCommand.Execute(row);
            Pump();

            AssertInView(view, scroller, row);
            return Task.CompletedTask;
        });

    private (WorkspacesPanelViewModel Panel, WorkspaceService Service) APanelOfForty()
    {
        var workspaces = new WorkspaceService(Path.Combine(_dir.Path, "workspaces.json"));
        for (var i = 0; i < 40; i++)
            workspaces.AddWorkspace(Path.Combine(_dir.Path, $"ws{i:00}"), $"Workspace {i:00}");

        var settings = new SettingsService(Path.Combine(_dir.Path, "settings.json"));
        return (new WorkspacesPanelViewModel(workspaces, settings), workspaces);
    }

    private static (WorkspacesPanelView View, ScrollViewer Scroller) Shown(WorkspacesPanelViewModel panel)
    {
        var view = new WorkspacesPanelView { DataContext = panel, Width = 240 };
        var window = new Window { Content = view, Width = 240, Height = 260 };
        window.Show();
        Pump();

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.GetVisualDescendants().OfType<ItemsControl>().Any(c => c.Name == "WorkspaceList"));
        return (view, scroller);
    }

    private static void AssertInView(WorkspacesPanelView view, ScrollViewer scroller, WorkspaceItemViewModel item)
    {
        var list = view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "WorkspaceList");
        var container = list.ContainerFromItem(item);
        Assert.NotNull(container);

        var top = Avalonia.VisualExtensions.TranslatePoint(container!, default, scroller)!.Value.Y;
        Assert.InRange(top, 0, scroller.Viewport.Height);
    }
}
