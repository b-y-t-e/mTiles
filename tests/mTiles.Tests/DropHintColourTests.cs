using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.Services;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A drop into the window's layout and a drop into a workspace look the same except in colour, and the
/// colour is always there to tell them apart.
/// </summary>
/// <remarks>The two levels are drawn one inside the other, so the same bands over the same part of the
/// screen can mean either. Nothing else on a hint can say which, so a theme in which the two colours came
/// out alike would be a gesture whose meaning the user has to guess.</remarks>
public class DropHintColourTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DropHintColourTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Every built-in theme, dark and light, gives the two levels different colours.</summary>
    [Fact]
    public void Every_theme_tells_the_window_from_a_workspace() => OnUiThread(() =>
    {
        var app = Avalonia.Application.Current!;
        try
        {
            foreach (var theme in TerminalTheme.BuiltIn)
            {
                ThemeBridge.Apply(theme);

                var window = Colour(app.Resources[DropHintBrushes.WindowKey]);
                var workspace = Colour(app.Resources[DropHintBrushes.WorkspaceKey]);

                Assert.NotEqual(workspace, window);
                // The workspace keeps the accent its hints have always been painted in.
                Assert.Equal(Colour(app.Resources["AccentHover"]), workspace);
            }
        }
        finally
        {
            ThemeBridge.Apply(TerminalTheme.GetByName(null));
        }
    });

    /// <summary>The window's surface paints in the window's colour; a workspace's keeps the default.</summary>
    [Fact]
    public void Each_surface_paints_its_own_level() => OnUiThread(() =>
    {
        var window = new MainWindow { Width = 800, Height = 600 };
        window.Show();
        try
        {
            var windowSurface = window.GetVisualDescendants().OfType<TileDropSurface>()
                .First(surface => surface.Name == "WindowSurface");
            Assert.Equal(DropHintBrushes.WindowKey, windowSurface.HintBrushKey);

            Assert.Equal(DropHintBrushes.WorkspaceKey, new TileDropSurface().HintBrushKey);
            Assert.Equal(DropHintBrushes.WorkspaceKey,
                new WorkspaceView().FindControl<TileDropSurface>("DropSurface")!.HintBrushKey);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The brushes come from the key they are asked for, at the weights every hint shares.</summary>
    [Fact]
    public void A_hint_is_painted_from_the_colour_it_is_given() => OnUiThread(() =>
    {
        var host = new Border();
        host.Resources[DropHintBrushes.WindowKey] = new SolidColorBrush(Color.FromRgb(200, 60, 180));

        var (fill, outline) = DropHintBrushes.For(host, DropHintBrushes.WindowKey);

        Assert.Equal(Color.FromArgb(55, 200, 60, 180), Colour(fill));
        Assert.Equal(Color.FromArgb(140, 200, 60, 180), Colour(outline));
    });

    private static Color Colour(object? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
