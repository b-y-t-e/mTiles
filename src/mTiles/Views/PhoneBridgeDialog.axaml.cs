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
/// <para>An overlay in the main window like every other dialog here. It was a window of its own, on the
/// argument that it has to be readable from arm's length while somebody holds a phone up to it — an
/// argument a tiling window manager settles the other way: on Hyprland the panel is placed into the
/// layout at whatever size the tiling decides, which is neither large nor where the user is looking.
/// Centred over a dimmed application it is at least always in the same place.</para>
/// <para>What went with the change is <c>KeepOnScreen</c>: capping the panel to its screen and
/// re-centring it as it grew was work only a free-floating window needed. An overlay is clamped to the
/// window it is drawn in by <see cref="OverlayHost"/>, once, for every dialog.</para>
/// <para>The codes it shows are still secrets whose lifetime is exactly this panel's.</para>
/// </remarks>
public partial class PhoneBridgeDialog : UserControl
{
    private PhoneBridgeViewModel? _model;

    public PhoneBridgeDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    internal static async Task ShowAsync(Visual owner, PhoneBridgeManager manager)
    {
        if (OverlayHost.For(owner) is not { } host)
            return;

        var model = new PhoneBridgeViewModel(manager);
        var window = new PhoneBridgeDialog { DataContext = model, _model = model };

        // Wired from here for the same reason ConfirmAction is: the clipboard belongs to a window —
        // which this control no longer is, so it asks the one it is drawn in.
        model.CopyToClipboard = async text =>
        {
            if (TopLevel.GetTopLevel(window)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        // Started after the window is on screen, so the first thing the user sees is the panel rather
        // than a frozen main window: discovery shells out to Tailscale and can take a second or two.
        //
        // Wrapped, because this is an async void handler: anything escaping it reaches the dispatcher's
        // unhandled-exception path, where it is a crash rather than a message. The panel has a place to
        // put a failure and is the right place to see one — the whole reason it is on screen is that
        // something about the network is being attempted.
        window.AttachedToVisualTree += async (_, _) =>
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

        // The panel grows as it learns things — a firewall verdict, a Tailscale hint, a startup
        // failure each add a block. As a window that needed re-centring and a cap against the screen
        // on every size change; as an overlay it is centred and clamped by OverlayHost for free.
        await host.ShowAsync<object>(window, width: 900);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => OverlayHost.CloseWith(this, null);

    /// <summary>What <c>OnClosed</c> was before this stopped being a window — see
    /// <see cref="SpeechSetupWizard"/> for why leaving the visual tree is the equivalent.</summary>
    protected override async void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

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
