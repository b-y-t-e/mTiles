using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The list of workspaces takes the shape its tile's size allows: rows, a strip of initials, or tabs.
/// </summary>
/// <remarks>By its size and not by where it was dropped, which is the rule the strip of initials followed
/// before the list could move: nothing is stored, so nothing can disagree with what is on screen.</remarks>
public class WorkspacesPanelShapeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public WorkspacesPanelShapeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspacesPanelShapeTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A table rather than a theory, because the shape is internal to the view layer.</summary>
    [Fact]
    public void The_shape_follows_the_size()
    {
        (double Width, double Height, WorkspacesPanelShape Expected)[] cases =
        [
            // Beside the layout, as the list has always stood.
            (240, 700, WorkspacesPanelShape.List),
            // Squeezed narrower than a name.
            (56, 700, WorkspacesPanelShape.Strip),
            // Along the top: the strip a drop gives it.
            (1200, 40, WorkspacesPanelShape.Tabs),
            // Short and narrow at once is still too short for a column of anything.
            (56, 40, WorkspacesPanelShape.Tabs),
            // Just tall enough for rows again.
            (240, 120, WorkspacesPanelShape.List),
            // Not laid out yet is not short: no one-frame spring into tabs.
            (0, 0, WorkspacesPanelShape.List),
        ];

        foreach (var (width, height, expected) in cases)
            Assert.Equal(expected, WorkspacesPanelShapes.For(new Size(width, height)));
    }

    /// <summary>One tab per workspace the filter lets through, and only the tabs on screen.</summary>
    [Fact]
    public void A_short_list_is_a_row_of_tabs_and_a_tall_one_is_rows_again() => OnUiThread(() =>
    {
        var workspaces = new WorkspaceService(Path.Combine(_dir, "workspaces.json"));
        for (var i = 0; i < 3; i++)
            workspaces.AddWorkspace(Path.Combine(_dir, $"ws{i}"), $"Workspace {i}");

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        using var panel = new WorkspacesPanelViewModel(workspaces, settings);

        EnsureControlThemes();
        var view = new WorkspacesPanelView { DataContext = panel, Width = 900, Height = 40 };
        var window = new Window { Content = view, Width = 900, Height = 40 };
        window.Show();
        Settle(window);

        try
        {
            Assert.True(view.FindControl<Control>("TabsPanel")!.IsVisible);
            Assert.False(view.FindControl<Control>("ExpandedPanel")!.IsVisible);
            Assert.False(view.FindControl<Control>("CollapsedPanel")!.IsVisible);

            var tabs = view.FindControl<ItemsControl>("TabWorkspaceList")!;
            Assert.Equal(panel.FilteredWorkspaces.Count, tabs.GetRealizedContainers().Count());
            Assert.Equal(3, panel.FilteredWorkspaces.Count);

            view.Height = 600;
            window.Height = 600;
            Settle(window);

            Assert.False(view.FindControl<Control>("TabsPanel")!.IsVisible);
            Assert.True(view.FindControl<Control>("ExpandedPanel")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The Fluent control themes, without which an <c>ItemsControl</c> has no template and so
    /// no panel to put a tab in — the same arrangement <c>WorkspaceRevealTests</c> makes.</summary>
    private static void EnsureControlThemes()
    {
        var app = Avalonia.Application.Current;
        if (app == null || app.Styles.Any(s => s is Avalonia.Themes.Fluent.FluentTheme)) return;
        app.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
    }

    /// <summary>Lays the window out until it stops changing.</summary>
    /// <remarks>The shape is decided when the panel learns its size, which is itself a layout pass, and the
    /// panel it shows is only measured — and its tabs only realised — on the pass after that.</remarks>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
