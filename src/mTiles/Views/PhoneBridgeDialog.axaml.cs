using System.Diagnostics;
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

        await window.ShowDialog(owner);
    }

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
