using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
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

    /// <summary>The answer a dialog closed with is the answer its caller gets.</summary>
    /// <remarks>
    /// <para>The gap this file was written for, one layer down: nothing exercised <c>CloseWith</c> with
    /// a result, so a whole suite passed while every dialog in the application answered <c>null</c>.
    /// Removing the entry from the host detaches it <em>synchronously</em> — Avalonia raises
    /// <c>OnDetachedFromVisualTree</c> on the same call stack, not through the dispatcher — and the
    /// override there completes the same <c>TaskCompletionSource</c> with <c>null</c>. Whichever
    /// happens first wins, because <c>TrySetResult</c> succeeds once.</para>
    /// <para>What it cost: <c>MessageDialog.ConfirmAsync</c> answering false however the user
    /// answered, so <b>Yes on every confirmation behaved as No</b> — discard, delete, push, tag —
    /// and <c>InputDialog</c> returning null instead of the typed text.</para>
    /// </remarks>
    [Theory]
    [InlineData("typed text")]
    [InlineData(true)]
    public void A_dialog_answers_with_what_it_was_closed_with(object answer) => OnUiThread(async () =>
    {
        var (window, host) = Scene();
        var content = new UserControl { Content = new TextBlock { Text = "body" } };

        var asked = host.ShowAsync<object>(content, width: 200);
        window.UpdateLayout();

        OverlayHost.CloseWith(content, answer);

        Assert.Equal(answer, await asked);

        window.Close();
    });

    /// <summary>A dialog dismissed rather than answered still answers, with nothing.</summary>
    /// <remarks>The other half of the same rule, so a fix that made the result stick cannot do it by
    /// leaving the Escape and X paths hanging for ever.</remarks>
    [Fact]
    public void A_dismissed_dialog_answers_with_nothing() => OnUiThread(async () =>
    {
        var (window, host) = Scene();
        var content = new UserControl { Content = new TextBlock { Text = "body" } };

        var asked = host.ShowAsync<object>(content, width: 200);
        window.UpdateLayout();

        OverlayHost.CloseWith(content, null);

        Assert.Null(await asked);

        window.Close();
    });

    /// <summary>A window taken down with a dialog still on it does not leave its caller waiting.</summary>
    /// <remarks>What <c>OnDetachedFromVisualTree</c> is actually for. It is also the reason the answer
    /// has to be set before the entry is removed rather than the override being deleted.</remarks>
    [Fact]
    public void Closing_the_window_answers_an_open_dialog() => OnUiThread(async () =>
    {
        var (window, host) = Scene();
        var content = new UserControl { Content = new TextBlock { Text = "body" } };

        var asked = host.ShowAsync<object>(content, width: 200);
        window.UpdateLayout();

        window.Close();

        Assert.Null(await asked);
    });

    /// <summary>Yes means yes, pressed on the real dialog.</summary>
    /// <remarks>
    /// The one above proves the mechanism; this proves the thing the user loses when it breaks. Every
    /// destructive action in the application is behind this call — discarding a file's changes,
    /// deleting a speech model, removing a workspace, undoing a commit — and the failure was silent in
    /// the safe direction, so it reads as a button that does nothing rather than as a fault.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Confirming_answers_with_the_button_that_was_pressed(bool yes) => OnUiThread(async () =>
    {
        var (window, _) = Scene();

        var asked = MessageDialog.ConfirmAsync(window, "Discard", "Throw the changes away?",
            whenUnavailable: false);
        window.UpdateLayout();

        var dialog = window.GetVisualDescendants().OfType<MessageDialog>().Single();
        var button = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => (string?)b.Content == (yes ? "Yes" : "No"));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(yes, await asked);

        window.Close();
    });

    private static void OnUiThread(Action body) => OnUiThread(() => { body(); return Task.CompletedTask; });

    /// <summary>The same, for a body that has to wait on the dispatcher.</summary>
    /// <remarks>A dialog's answer arrives through <c>RunContinuationsAsynchronously</c> onto the UI
    /// thread's own queue, so it is never ready on the line after <c>CloseWith</c> — asserting
    /// <c>IsCompleted</c> there measures the scheduler and not the result.</remarks>
    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(OverlayHostTests).Assembly);
        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
