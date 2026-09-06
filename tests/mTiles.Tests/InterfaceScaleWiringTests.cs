using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using mTiles.Services;
using mTiles.Views;
using System.Linq;
using Xunit;

namespace mTiles.Tests;

/// <summary>That the scale in Settings actually reaches the window.</summary>
/// <remarks>
/// <para>The lesson <see cref="OverlayHostTests"/> was written for, applied to a second feature: a
/// setting that is stored, normalised and never drawn passes every test about the number and fails at
/// the one thing it is for. So this builds the real <c>MainWindow</c> from its real markup, and
/// measures what the transform did to the content — a named control that has stopped being in the tree,
/// or a transform assigned to the wrong thing, is a failure here rather than a screenshot somebody
/// takes a week later.</para>
/// <para>Headless, so the window has a size and lays out but never appears.</para>
/// </remarks>
public class InterfaceScaleWiringTests
{
    [Fact]
    public void The_whole_window_is_drawn_through_one_scale() => OnUiThread(() =>
    {
        var window = new MainWindow { Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();

        var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().FirstOrDefault();
        Assert.NotNull(host);

        // Everything is inside it — the workspaces panel, the tiles and the dialogs alike. A scaled
        // interface with an unscaled Settings drawn over it is the failure this asserts against.
        var grid = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "MainGrid");
        var overlays = window.GetVisualDescendants().OfType<OverlayHost>().Single();
        Assert.Contains(host, grid.GetVisualAncestors());
        Assert.Contains(host, overlays.GetVisualAncestors());

        var unscaled = grid.Bounds.Width;
        Assert.True(unscaled > 0, "The window laid out to nothing before any scale was applied.");

        host.LayoutTransform = new ScaleTransform(2, 2);
        window.UpdateLayout();

        // Twice as large on screen means half as many device-independent pixels of room, which is what
        // makes the text bigger rather than merely the window's content wider.
        Assert.InRange(grid.Bounds.Width, unscaled / 2 - 1, unscaled / 2 + 1);
    });

    /// <summary>The stored scale is applied when the window is bound, without anything else happening.</summary>
    [Fact]
    public void A_stored_scale_is_applied_on_startup() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        settings.Service.Settings.UiScale = 1.5;

        var window = new MainWindow { Width = 800, Height = 600 };
        window.Show();
        window.BindWindowState(settings.Service);
        window.UpdateLayout();

        var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        var scale = Assert.IsType<ScaleTransform>(host.LayoutTransform);
        Assert.Equal(1.5, scale.ScaleX);
        Assert.Equal(1.5, scale.ScaleY);
    });

    /// <summary>Turning the spinner in Settings changes the window while it is open.</summary>
    /// <remarks>Through <c>SettingsChanged</c>, which is what makes the scale visible as it is turned
    /// rather than at the next launch — and it is also the subscription that would be silently missing
    /// if the value were only read once at startup.</remarks>
    [Fact]
    public void Changing_it_in_settings_changes_the_window() => OnUiThread(() =>
    {
        using var settings = new TempSettings();

        var window = new MainWindow { Width = 800, Height = 600 };
        window.Show();
        window.BindWindowState(settings.Service);

        settings.Service.Settings.UiScale = 0.75;
        settings.Service.NotifyChanged();
        window.UpdateLayout();

        var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        Assert.Equal(0.75, Assert.IsType<ScaleTransform>(host.LayoutTransform).ScaleX);
    });

    /// <summary>A scale no settings file should hold still leaves a window somebody can undo it in.</summary>
    [Fact]
    public void A_scale_of_zero_does_not_empty_the_window() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        settings.Service.Settings.UiScale = 0;

        var window = new MainWindow { Width = 800, Height = 600 };
        window.Show();
        window.BindWindowState(settings.Service);
        window.UpdateLayout();

        var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
        Assert.Equal(InterfaceScale.Default, Assert.IsType<ScaleTransform>(host.LayoutTransform).ScaleX);
    });

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(InterfaceScaleWiringTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
