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
    private readonly string _dir = Directory.CreateTempSubdirectory("mtiles-reveal").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* not a test failure */ }
    }

    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspaceRevealTests).Assembly);
        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

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

    /// <summary>Gives the headless session the control themes an <c>ItemsControl</c> needs.</summary>
    /// <remarks>The test application carries no styles, so without this an <c>ItemsControl</c> has no
    /// template, no <c>ItemsPresenter</c> and therefore no containers — it lays out 224x0 with 41 items
    /// in it, and every assertion here would be about the harness rather than about the panel. Added
    /// once and left in place: the session is shared by the whole assembly.</remarks>
    private static void EnsureControlThemes()
    {
        var app = Avalonia.Application.Current;
        if (app == null || app.Styles.Any(s => s is Avalonia.Themes.Fluent.FluentTheme)) return;
        app.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
    }

    [Fact]
    public void A_workspace_added_below_the_fold_is_scrolled_to()
        => OnUiThread(async () =>
        {
            var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
            // Enough rows that the last one cannot be on screen in a 260px panel, and named so the one
            // added below sorts to the end rather than into the middle.
            for (var i = 0; i < 40; i++)
                workspaces.AddWorkspace(Path.Combine(_dir, $"ws{i:00}"), $"Workspace {i:00}");

            var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
            var panel = new WorkspacesPanelViewModel(workspaces, settings);

            EnsureControlThemes();

            var view = new WorkspacesPanelView { DataContext = panel, Width = 240 };
            var window = new Window { Content = view, Width = 240, Height = 260 };
            window.Show();
            Pump();

            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.GetVisualDescendants().OfType<ItemsControl>().Any(c => c.Name == "WorkspaceList"));
            Assert.Equal(0, scroller.Offset.Y);

            var added = Path.Combine(_dir, "zz-last");
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
}
