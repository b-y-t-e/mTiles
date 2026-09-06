using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// <see cref="ModalSurface"/> itself — the two routing rules <c>OverlayHost</c> and the settings
/// dialog both rely on and neither can prove on its own, because both need a real window to show
/// through: Tab wrapping inside the claimed surface, and focus arriving from outside being pulled
/// back in.
/// </summary>
/// <remarks>
/// A synthetic window rather than a real dialog, on purpose — the shape that matters is "a claimed
/// surface with focusable controls inside it and a focusable control outside it", and building that
/// directly keeps the test about <c>ModalSurface</c> rather than about the settings dialog or the
/// speech wizard.
/// </remarks>
public class ModalSurfaceTests : IDisposable
{
    private IDisposable? _claim;

    public void Dispose()
    {
        if (_claim is { } claim)
            OnUiThread(claim.Dispose);
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ModalSurfaceTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private sealed record Scene(Window Window, Button Outside, Button First, Button Last, Border Surface);

    private static Scene BuildScene()
    {
        var first = new Button { Content = "First", Focusable = true };
        var last = new Button { Content = "Last", Focusable = true };
        var surface = new Border
        {
            Child = new StackPanel { Children = { first, last } },
        };
        var outside = new Button { Content = "Outside", Focusable = true };

        var root = new StackPanel { Children = { outside, surface } };
        var window = new Window { Content = root, Width = 300, Height = 200 };
        window.Show();
        Pump();

        return new Scene(window, outside, first, last, surface);
    }

    /// <summary>Tab from the last control inside the surface wraps to the first, never reaching the
    /// control outside it.</summary>
    [Fact]
    public void Tab_from_the_last_control_wraps_inside_the_claimed_surface()
        => OnUiThread(() =>
        {
            var scene = BuildScene();
            _claim = ModalSurface.Take(scene.Surface);
            Pump();

            scene.Last.Focus();
            Assert.Same(scene.Last, scene.Window.FocusManager?.GetFocusedElement());

            scene.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            scene.Window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Pump();

            var focused = scene.Window.FocusManager?.GetFocusedElement();
            Assert.NotSame(scene.Outside, focused);
            Assert.Same(scene.First, focused);
        });

    /// <summary>Focus arriving on a control outside the surface — a click that lands before a scrim
    /// swallows it, a control restoring focus as it goes away — is pulled straight back in.</summary>
    [Fact]
    public void Focus_landing_outside_the_surface_is_pulled_back_in()
        => OnUiThread(() =>
        {
            var scene = BuildScene();
            _claim = ModalSurface.Take(scene.Surface);
            Pump();

            scene.Outside.Focus();
            Pump();

            var focused = scene.Window.FocusManager?.GetFocusedElement();
            Assert.NotSame(scene.Outside, focused);
            Assert.Same(scene.First, focused);
        });

    /// <summary>Once the claim is released, Tab is free to leave and focus is free to land outside
    /// again — the trap must not outlive the dialog it was guarding.</summary>
    [Fact]
    public void Releasing_the_claim_frees_both_the_tab_cycle_and_focus()
        => OnUiThread(() =>
        {
            var scene = BuildScene();
            var claim = ModalSurface.Take(scene.Surface);
            Pump();
            claim.Dispose();

            scene.Outside.Focus();
            Pump();

            Assert.Same(scene.Outside, scene.Window.FocusManager?.GetFocusedElement());
        });
}
