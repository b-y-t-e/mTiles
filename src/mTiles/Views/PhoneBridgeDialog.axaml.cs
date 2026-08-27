using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using mTiles.Services.Phone;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The QR panel: what to point a phone at, and what to do when it does not work.
/// </summary>
/// <remarks>
/// A window rather than an overlay in the main window, because it has to be readable from arm's length
/// while somebody holds a phone up to it — and because the codes it shows are secrets whose lifetime is
/// exactly this window's.
/// </remarks>
public partial class PhoneBridgeDialog : Window
{
    private PhoneBridgeViewModel? _model;

    public PhoneBridgeDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    internal static async Task ShowAsync(Window owner, PhoneBridgeManager manager)
    {
        var model = new PhoneBridgeViewModel(manager);
        var window = new PhoneBridgeDialog { DataContext = model, _model = model };

        // Wired from here for the same reason ConfirmAction is: the clipboard belongs to a window.
        model.CopyToClipboard = async text =>
        {
            if (window.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        // Started after the window is on screen, so the first thing the user sees is the panel rather
        // than a frozen main window: discovery shells out to Tailscale and can take a second or two.
        //
        // Wrapped, because this is an async void handler: anything escaping it reaches the dispatcher's
        // unhandled-exception path, where it is a crash rather than a message. The panel has a place to
        // put a failure and is the right place to see one — the whole reason it is on screen is that
        // something about the network is being attempted.
        window.Opened += async (_, _) =>
        {
            try
            {
                await model.InitializeAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("The phone panel could not start the bridge: {0}", ex);
                model.ReportStartupFailure(ex.Message);
            }
        };

        // CenterOwner places the window once, at the height it opens with. This one grows: a
        // firewall verdict, a Tailscale hint or a startup failure each add a block to it, and
        // SizeToContent grows a window downwards from where it already is - so the bottom of the panel,
        // which is where those messages arrive, went off the bottom of the screen exactly when there
        // was something new to read there. Re-centred on every size change, and capped to the screen
        // it is on, so it cannot grow past what the screen can show in the first place.
        window.Opened += (_, _) => KeepOnScreen(window);
        window.SizeChanged += (_, _) => KeepOnScreen(window);

        await window.ShowDialog(owner);
    }

    /// <summary>Caps the window to its screen and centres it there.</summary>
    /// <remarks>
    /// <para>Centred on the screen's working area rather than on the owner: the owner can be
    /// half off screen, maximised across two monitors, or smaller than this panel, and centring on it
    /// then puts a window the user has to read at arm's length half under a taskbar. The screen it is
    /// already on is the one the user is looking at.</para>
    /// <para>The cap is applied before the position is worked out, because a window taller than the
    /// screen has no position that shows all of it - and <see cref="Window.MaxHeight"/> in DIPs against
    /// a working area in device pixels is the one conversion here that has to be right on a scaled
    /// display.</para>
    /// </remarks>
    private static void KeepOnScreen(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        // Room for the frame the window manager draws around this, which is not in Bounds.
        var cap = area.Height / scaling - 48;
        if (cap > 0) window.MaxHeight = Math.Min(TallestUseful, cap);

        var size = PixelSize.FromSize(window.FrameSize ?? window.Bounds.Size, scaling);
        var at = window.Position;

        // Only when it has to. This runs on every size change, and a panel that grew by one firewall
        // message while the user had dragged it somewhere they wanted it would otherwise jump back to
        // the middle — a window moving under the pointer for a reason nobody can see. Fits where it is:
        // leave it. Hangs off an edge: put it back in the middle, which is the one position that is
        // right whatever the size.
        var fits = at.X >= area.X && at.Y >= area.Y
                   && at.X + size.Width <= area.X + area.Width
                   && at.Y + size.Height <= area.Y + area.Height;

        if (fits) return;

        window.Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2));
    }

    /// <summary>
    /// Beyond this the panel is not easier to read, only longer to scan - the codes are at the top and
    /// the troubleshooting below them scrolls.
    /// </summary>
    /// <remarks>
    /// The same number is in the markup, and deliberately: this method returns early when the window
    /// manager will not say which screen the window is on, and without a cap in the XAML a panel that
    /// grew — a firewall verdict, a Tailscale hint — would then have nothing at all to stop it. Two
    /// copies of a constant is the smaller fault; the other one is a window taller than the desktop
    /// with its own close button below the edge of it.
    /// </remarks>
    private const double TallestUseful = 880;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_model is not { } model)
            return;

        _model = null;

        // Awaited so the shutdown runs in order, but this is an async void handler: it returns to Avalonia
        // at the first await, so nothing here can promise that closing has *finished* before the user
        // reopens the panel. What makes that safe is not this method — it is the lifecycle semaphore in
        // PhoneBridgeManager, which serialises start against stop whatever order they arrive in. An
        // earlier version of this comment claimed the ordering guarantee outright, which was wrong.
        //
        // Wrapped because it is an async void: there is no panel left to report into, which is precisely
        // why it must not throw.
        try { await model.CloseAsync(); }
        catch (Exception ex) { Trace.TraceWarning("The phone panel did not close cleanly: {0}", ex); }
        finally { model.Dispose(); }
    }
}
