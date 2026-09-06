using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using mTiles.Views;
using System.Linq;
using Xunit;

namespace mTiles.Tests;

/// <summary>That a dialog handed to <see cref="OverlayHost"/> actually reaches the screen.</summary>
/// <remarks>
/// The guard that was missing. Settings stopped opening at all and every one of the 2350 tests still
/// passed: <c>ApplySettingsDialog</c> wraps the show in a try/catch — it is an <c>async void</c>
/// handler and must not throw at the dispatcher — so a dialog that failed to open looked exactly like
/// one that was never asked for. What failed was the header: a control that already had a parent,
/// handed to the host to draw in a second place, which Avalonia refuses.
/// </remarks>
public class OverlayHostTests
{
    private sealed class WithHeader : UserControl, OverlayHost.IOverlayHeader
    {
        public WithHeader() => Content = new TextBlock { Text = "body" };

        /// <summary>A fresh control, which is the rule this exists to hold: a parented one throws.</summary>
        public Control? OverlayHeader => new TextBlock { Text = "header" };
    }

    private static (Window Window, OverlayHost Host) Scene()
    {
        var host = new OverlayHost();
        var window = new Window { Content = new Panel { Children = { host } }, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
        return (window, host);
    }

    [Fact]
    public void A_dialog_with_a_header_of_its_own_is_drawn() => OnUiThread(() =>
    {
        var (window, host) = Scene();
        var content = new WithHeader();

        _ = host.ShowAsync<object>(content, width: 200);
        window.UpdateLayout();

        Assert.Contains(window.GetVisualDescendants(), v => ReferenceEquals(v, content));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "header");

        window.Close();
    });

    [Fact]
    public void The_settings_page_supplies_a_header_that_has_no_parent() => OnUiThread(() =>
    {
        // The exact shape of the fault: asking twice must give two controls, because the host puts
        // what it is given into a tree of its own. SettingsView answering with one piece of its own
        // page is what closed the dialog the instant it opened.
        var view = new SettingsView();

        var first = ((OverlayHost.IOverlayHeader)view).OverlayHeader;
        var second = ((OverlayHost.IOverlayHeader)view).OverlayHeader;

        Assert.NotNull(first);
        Assert.NotSame(first, second);
        Assert.Null(first!.Parent);
    });

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(OverlayHostTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
