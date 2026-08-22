using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services;
using mTiles.Services.Phone;

namespace mTiles.ViewModels;

/// <summary>One QR code on the panel: an address, why it was chosen, and the code itself.</summary>
internal sealed partial class PhoneCodeViewModel(
    RankedPhoneEndpoint ranked, string url, string token, Bitmap? image, bool certificateTrusted)
    : ObservableObject
{
    /// <summary>The pairing token inside <see cref="Url"/>, so the panel can ask whether it still works.</summary>
    public string Token { get; } = token;

    public PhoneEndpoint Endpoint { get; } = ranked.Endpoint;

    /// <summary>Which of the two audiences this is. The panel shows it as an icon before it says it in words.</summary>
    public bool IsRemote => ranked.Audience == PhoneEndpointAudience.Remote;

    /// <summary>
    /// Two or three words, not a sentence.
    /// </summary>
    /// <remarks>
    /// This is read from wherever the user is holding their phone, which is not where they read the rest
    /// of the dialog from. "Phone on the same Wi-Fi" and "Phone on another network" share their first two
    /// words and differ at the end, so at a glance the two cards looked identical — and picking the wrong
    /// one is the failure this panel exists to prevent.
    /// </remarks>
    public string Title => IsRemote ? "Another network" : "Same Wi-Fi";

    /// <summary>
    /// Whether this card puts its QR code on the left.
    /// </summary>
    /// <remarks>
    /// Alternated down the grid, so the left card's code sits against the left edge and the right card's
    /// against the right. It is the one deliberately asymmetric thing in this dialog and it is there for a
    /// physical reason: a phone camera held at arm's length covers roughly a third of the screen, so two
    /// centred codes fall inside one frame together and the wrong one gets decoded. Pushing them outward
    /// puts about two hundred more pixels between them, which is the difference between the camera seeing
    /// a choice and seeing one code.
    /// </remarks>
    [ObservableProperty] private bool _codeOnLeft = true;

    /// <summary>
    /// Which edge the code sits against, as a dock rather than a grid column.
    /// </summary>
    /// <remarks>
    /// A <c>Grid</c> cannot express this. Swapping the column *indexes* while the column *widths* stayed
    /// put meant that whenever the code moved to the starred column, the text landed in the auto-sized one
    /// and took everything it wanted — the star collapsed, and the QR code was drawn as a white sliver
    /// hanging off the edge of the card. A <c>DockPanel</c> gives the docked child its natural size and
    /// hands the rest to the fill child, whichever side it is on, which is exactly the shape wanted here.
    /// </remarks>
    public Dock CodeDock => CodeOnLeft ? Dock.Left : Dock.Right;

    /// <summary>The gutter between the code and the text, on whichever side the text is.</summary>
    public Thickness CodeMargin => CodeOnLeft ? new Thickness(0, 0, 14, 0) : new Thickness(14, 0, 0, 0);

    partial void OnCodeOnLeftChanged(bool value)
    {
        OnPropertyChanged(nameof(CodeDock));
        OnPropertyChanged(nameof(CodeMargin));
    }

    public string Host => ranked.Endpoint.Host;

    public string Reason => ranked.Reason;

    public Bitmap? Image { get; } = image;

    public string Url { get; } = url;

    /// <summary>
    /// Puts the address on the clipboard, for opening in a browser on this machine.
    /// </summary>
    /// <remarks>
    /// A button rather than the URL printed in full. Spelled out it ran to four wrapped lines of
    /// base64 in the middle of a card whose job is to be read at a glance — the longest and least
    /// legible thing on screen, for a case (typing it into the near machine's browser) that is real but
    /// secondary. The QR code is the primary form of the same information.
    /// </remarks>
    [RelayCommand]
    private async Task CopyLink()
    {
        if (CopyToClipboard is { } copy)
            await copy(Url);
    }

    /// <summary>Wired from the panel, which is wired from the window: only a window has a clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// Whether the phone will show a certificate warning before the page opens.
    /// </summary>
    /// <remarks>
    /// Said in advance rather than left as a surprise. A full-page security warning on a phone reads as
    /// "this is broken, back out", which is the one reaction that makes the feature fail — and the user
    /// has to click through it to be given a microphone at all.
    /// </remarks>
    public bool WarnsAboutCertificate => !certificateTrusted;

    /// <summary>The opposite, so the reassuring case can be shown as plainly as the warning one.</summary>
    public bool IsTrusted => certificateTrusted;
}

/// <summary>An address in the "different network?" list.</summary>
internal sealed partial class PhoneAlternativeViewModel(RankedPhoneEndpoint ranked, Action<RankedPhoneEndpoint> show)
    : ObservableObject
{
    public string Host => ranked.Endpoint.Host;
    public string Reason => ranked.Reason;

    [RelayCommand]
    private void Show() => show(ranked);
}

/// <summary>A phone that has paired.</summary>
internal sealed partial class PhoneDeviceViewModel(PhoneSession session, Action<PhoneSession> revoke)
    : ObservableObject
{
    public string Label => session.Label;
    public string Since => $"paired {session.Established.ToLocalTime():HH:mm}";

    [RelayCommand]
    private void Revoke() => revoke(session);
}

/// <summary>
/// The panel that turns the bridge on and shows the codes to scan.
/// </summary>
/// <remarks>
/// Opening the panel starts the bridge and closing it stops it again, unless the user has asked in
/// Settings for it to keep running or a phone is still paired. That is the least-exposure default: the
/// one server in this application that listens to the network is listening while it is being used, and
/// not merely because mTiles is open.
/// </remarks>
internal sealed partial class PhoneBridgeViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long to wait before offering firewall help.
    /// </summary>
    /// <remarks>
    /// Long enough that somebody scanning at a normal pace never sees it, short enough to arrive while
    /// they are still looking at the panel wondering why nothing happened. Offered rather than acted on:
    /// the repair needs elevation, and elevation prompts nobody asked for are how a program teaches users
    /// to click Yes without reading.
    /// </remarks>
    private static readonly TimeSpan TroubleAfter = TimeSpan.FromSeconds(20);

    /// <summary>How long closing waits for a start that is still in flight. See <see cref="CloseAsync"/>.</summary>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the codes on screen are replaced.
    /// </summary>
    /// <remarks>
    /// A pairing code lasts two minutes, and nothing was replacing it — so a panel left open showed a
    /// code that had quietly stopped working, and after twenty seconds offered the firewall as the
    /// explanation. That is the "I went to fetch my phone" case, which is most of the times this panel is
    /// used at all: the user comes back, scans, is told the code expired, and is pointed at the wrong
    /// cause. Comfortably inside the two minutes, so a code is never stale on screen.
    /// </remarks>
    private static readonly TimeSpan CodeRefresh = TimeSpan.FromSeconds(80);

    private readonly PhoneBridgeManager _manager;
    private readonly DispatcherTimer _troubleTimer;
    private readonly DispatcherTimer _codeTimer;
    private IDisposable? _hold;
    private bool _ready;
    private bool _disposed;

    /// <summary>
    /// The startup in flight, so closing can wait for it.
    /// </summary>
    /// <remarks>
    /// Closing the panel while it was still starting released the hold before the server existed, so the
    /// "may this stop now" check found nothing to stop — and the bridge then finished starting, with the
    /// panel gone and nothing holding it. It went on listening to the network against a setting that says
    /// not to, until something else happened to ask again.
    /// </remarks>
    private Task? _starting;

    public PhoneBridgeViewModel(PhoneBridgeManager manager)
    {
        _manager = manager;

        _codeTimer = new DispatcherTimer { Interval = CodeRefresh };
        _codeTimer.Tick += (_, _) =>
        {
            // Nothing to keep alive once a device is paired, and redrawing then would replace a code
            // somebody may be about to scan with a second device.
            if (_disposed || !_ready || Devices.Count > 0)
                return;

            Redraw();
        };

        _troubleTimer = new DispatcherTimer { Interval = TroubleAfter };
        _troubleTimer.Tick += (_, _) =>
        {
            _troubleTimer.Stop();
            if (Devices.Count == 0)
                ShowTrouble = true;
        };

        _manager.StateChanged += OnManagerChanged;
    }

    public ObservableCollection<PhoneCodeViewModel> Codes { get; } = [];
    public ObservableCollection<PhoneAlternativeViewModel> Alternatives { get; } = [];
    public ObservableCollection<PhoneDeviceViewModel> Devices { get; } = [];

    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _showTrouble;
    [ObservableProperty] private bool _showAlternatives;
    [ObservableProperty] private string _firewallResult = "";

    /// <summary>Says which port was really used, when it is not the one asked for.</summary>
    [ObservableProperty] private string _portNotice = "";

    public bool HasPortNotice => PortNotice.Length > 0;

    partial void OnPortNoticeChanged(string value) => OnPropertyChanged(nameof(HasPortNotice));

    /// <summary>
    /// The firewall advice, read after the bridge is up rather than in the constructor.
    /// </summary>
    /// <remarks>
    /// It has to name the port that was actually bound, and at construction time there is not one: the
    /// bridge has not started, and the port it ends up on may not be the port in Settings. The Linux
    /// advice is a command the user copies, so an out-of-date number there is a command that opens the
    /// wrong hole and leaves the real one shut.
    /// </remarks>
    [ObservableProperty] private string _firewallExplanation = "";
    [ObservableProperty] private string _manualFirewallCommand = "";
    [ObservableProperty] private bool _canRepairFirewall;

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Starts the bridge and draws the codes. Called when the panel opens.</summary>
    public Task InitializeAsync() => _starting = StartUpAsync();

    private async Task StartUpAsync()
    {
        IsBusy = true;
        Status = "Starting…";

        // Held for as long as the panel is on screen, so the bridge stays up while the user is looking
        // at a QR code even though the setting says not to keep it running.
        _hold ??= _manager.HoldOpen();

        await _manager.RefreshAsync();
        var started = await _manager.StartAsync();

        IsBusy = false;

        if (!started)
        {
            Error = _manager.LastError ?? "The bridge could not be started.";

            // Shown at once, not after the twenty-second wait. That timer is for "it started but nothing
            // has connected"; this is "it did not start", and leaving the panel with an error string and
            // no advice was the one case with nothing at all to act on.
            DescribeBridge();
            ShowTrouble = true;
            return;
        }


        Redraw();
        DescribeBridge();

        _troubleTimer.Start();
        _codeTimer.Start();
    }

    /// <summary>Shows a failure that got past <see cref="InitializeAsync"/>'s own handling.</summary>
    public void ReportStartupFailure(string message)
    {
        IsBusy = false;
        Status = "Not running";
        Error = message;
    }

    /// <summary>
    /// Looks at the machine's addresses again and reconfigures the bridge for whatever it finds.
    /// </summary>
    /// <remarks>
    /// The reconfiguration is the point, not the redraw. Re-ranking alone produced codes for addresses
    /// the running server had never been told about — its allowed hosts and its certificate are fixed
    /// when it starts — so the button that exists for "this is not working, look again" handed back a
    /// perfectly good QR code that answers <c>400</c>. StartAsync restarts only when the set actually
    /// differs, so pressing it when nothing has changed still costs nothing.
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Error = "";

        // Wrapped: this is a command, so anything escaping it reaches the dispatcher as a crash rather
        // than as a message, and the panel is the right place to read a failure about the network.
        try
        {
            await _manager.RefreshAsync();

            if (!await _manager.StartAsync())
                Error = _manager.LastError ?? "The bridge could not be restarted.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Re-checking the phone bridge failed: {0}", ex);
            Error = ex.Message;
        }

        IsBusy = false;
        Redraw();
        DescribeBridge();
    }

    /// <summary>
    /// Restates everything the panel says about the bridge as it is right now.
    /// </summary>
    /// <remarks>
    /// One method, called from both places that can change it, because the pieces are not independent:
    /// a restart can move the port, which changes the firewall command the user is invited to copy, the
    /// note about the substituted port, and whether the codes on screen warn. Recomputing some of them
    /// after "Look again" and not others left the panel describing the previous bridge — including a
    /// command that would have opened the wrong port and left the right one shut.
    /// </remarks>
    private void DescribeBridge()
    {
        // Follows the bridge rather than latching. It only ever went true, so a panel whose bridge had
        // since stopped went on describing itself as ready.
        _ready = _manager.IsRunning;

        var advice = _manager.Firewall.GetAdvice(_manager.ActivePort);
        FirewallExplanation = advice.Explanation;
        ManualFirewallCommand = advice.ManualCommand;
        CanRepairFirewall = advice.CanRepair;
        FirewallResult = "";

        // A substitution is worth saying out loud. Silently ignoring the configured port would leave
        // somebody reading Settings, seeing 18091, and looking for a bridge that is not there.
        PortNotice = _manager.PortWasSubstituted
            ? $"Port {_manager.Port} is not available on this machine — Windows reserves blocks of ports "
              + $"for Hyper-V and WSL — so the bridge is on {_manager.ActivePort} instead."
            : "";

        if (Error.Length > 0)
        {
            Status = "Not running";
            return;
        }

        // A connected device outranks everything: it is the answer to the question the panel was opened
        // to ask. RefreshDevices has already put it there, so it is not overwritten here.
        if (Devices.Count == 0)
            Status = ReadyStatus();
    }

    [RelayCommand]
    private async Task RepairFirewallAsync()
    {
        FirewallResult = "";
        var result = await _manager.RepairFirewallAsync();
        FirewallResult = result.Message;
    }

    [RelayCommand]
    private void ToggleAlternatives() => ShowAlternatives = !ShowAlternatives;

    /// <summary>
    /// Shuts the panel down: withdraws the displayed codes, and stops the bridge unless something still
    /// needs it.
    /// </summary>
    public async Task CloseAsync()
    {
        _troubleTimer.Stop();
        _codeTimer.Stop();
        _manager.StopShowingCodes();

        // Let the start finish before letting go. Releasing the hold first meant the stop check ran
        // against a bridge that had not come up yet, found nothing, and left it running once it did.
        if (_starting is { } starting)
        {
            _starting = null;

            // Bounded. The start shells out to `tailscale status` and asks the operating system to bind a
            // socket, and an unbounded await here holds the closing window open — and with it the
            // lifecycle lock the next start needs — for however long that takes. Ten seconds is far more
            // than a start ever needs and far less than "for ever".
            try { await starting.WaitAsync(StartupPatience); }
            catch (TimeoutException)
            {
                System.Diagnostics.Trace.TraceWarning("The phone bridge was still starting when its panel closed.");
            }
            catch { /* already reported into the panel, or logged by the caller */ }
        }

        // Releasing the hold is what may stop the bridge, and the manager decides whether it should: a
        // paired phone is somebody's working setup, and stopping the server under it because a window
        // was closed would be the panel undoing what the user just did with it. That rule lives in one
        // place now, because the setting being switched off has to obey the same one.
        var hold = _hold;
        _hold = null;
        hold?.Dispose();
    }

    /// <summary>
    /// True when any code on screen leads to a certificate the phone will object to.
    /// </summary>
    /// <remarks>
    /// Said once, under both cards, rather than repeated inside each. The same three lines under two
    /// codes was most of what made them look alike — and looking alike is exactly what makes somebody
    /// photograph the wrong one.
    /// </remarks>
    public bool ShowCertificateNote => Codes.Any(code => code.WarnsAboutCertificate);

    private void Redraw()
    {
        // Whatever was on screen stops working, because it is no longer on screen. Closing the panel
        // withdraws its codes for exactly this reason — a code somebody may have photographed should not
        // outlive the moment it was shown — and replacing them is the same act with the same argument.
        _manager.StopShowingCodes();

        DisposeCodes();
        Alternatives.Clear();
        _drawnForGeneration = _manager.Generation;

        // No codes for a bridge that is not listening. Drawing them anyway produced QR codes addressed
        // to port 0 — they scan perfectly and lead nowhere, which is a worse answer than the error the
        // panel is already showing.
        if (!_manager.IsRunning || _manager.ActivePort == 0)
        {
            RefreshDevices();
            OnPropertyChanged(nameof(ShowCertificateNote));
            return;
        }

        foreach (var ranked in _manager.Board.Recommended)
            Codes.Add(BuildCode(ranked));

        Relayout();

        foreach (var ranked in _manager.Board.All.Where(entry => Codes.All(c => c.Endpoint != entry.Endpoint)))
            Alternatives.Add(new PhoneAlternativeViewModel(ranked, Show));

        RefreshDevices();
    }

    /// <summary>Wired from the dialog, which has the window the clipboard belongs to.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private PhoneCodeViewModel BuildCode(RankedPhoneEndpoint ranked)
    {
        var (url, token) = _manager.BuildPairingUrl(ranked.Endpoint);
        return new PhoneCodeViewModel(
            ranked, url, token, QrCodeImage.Render(url), _manager.IsTrustedFor(ranked.Endpoint.Host))
        {
            CopyToClipboard = text => CopyToClipboard?.Invoke(text) ?? Task.CompletedTask,
        };
    }

    private void Show(RankedPhoneEndpoint ranked)
    {
        if (Codes.Any(code => code.Endpoint == ranked.Endpoint))
            return;

        Codes.Insert(0, BuildCode(ranked));
        Relayout();

        DropDeadCodes();
        Relayout();

        // FirstOrDefault, not First: two sources can report the same host, and a list rebuilt between
        // the click and this line need not still contain the row that was clicked.
        if (Alternatives.FirstOrDefault(a => a.Host == ranked.Endpoint.Host) is { } row)
            Alternatives.Remove(row);
    }

    /// <summary>
    /// Takes off screen any code that can no longer be redeemed.
    /// </summary>
    /// <remarks>
    /// Only so many pairing tokens may be live at once, and issuing one past that limit quietly
    /// invalidates the oldest; a code also simply expires after a couple of minutes. Either way what is
    /// left on screen scans perfectly and then says the pairing expired, which is worse than no code at
    /// all. Which codes are still good is <see cref="PhonePairing"/>'s to answer — the panel asks rather
    /// than keeping its own copy of the rule.
    /// </remarks>
    private void DropDeadCodes()
    {
        foreach (var dead in Codes.Where(code => !_manager.Pairing.IsPairingTokenLive(code.Token)).ToList())
        {
            Codes.Remove(dead);

            if (dead.Image is { } retired)
                Dispatcher.UIThread.Post(retired.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Releases the QR bitmaps, but not until the views holding them have gone.
    /// </summary>
    /// <remarks>
    /// Disposing them where they were built freed a <see cref="Bitmap"/> that the <c>Image</c> in the
    /// item template was still pointing at: the collection had been emptied, but the containers are not
    /// torn down until the next layout pass, and the render thread draws from the old ones in between.
    /// Clearing first and freeing afterwards, at background priority, means nothing is holding them by
    /// the time they go. Each is only a few hundred kilobytes — this is about not handing the renderer a
    /// disposed handle, not about the memory.
    /// </remarks>
    private void DisposeCodes()
    {
        var retired = Codes.Select(code => code.Image).OfType<Bitmap>().ToList();
        Codes.Clear();

        if (retired.Count == 0)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var bitmap in retired)
                    bitmap.Dispose();
            },
            DispatcherPriority.Background);
    }

    /// <summary>Mirrors the codes outward: even positions put the code on the left, odd on the right.</summary>
    private void Relayout()
    {
        for (var i = 0; i < Codes.Count; i++)
            Codes[i].CodeOnLeft = i % 2 == 0;

        OnPropertyChanged(nameof(ShowCertificateNote));
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var session in _manager.Sessions)
            Devices.Add(new PhoneDeviceViewModel(session, s => _manager.Pairing.Revoke(s.Id)));

        if (Devices.Count > 0)
        {
            ShowTrouble = false;
            _troubleTimer.Stop();
            // "Paired" is what is known; "connected" is only claimed when a socket is actually open.
            // A session outlives a change of network but the cookie carrying it does not — it was set for
            // the address the phone paired on — so a device can be perfectly paired and unable to reach
            // this machine, and saying "connected" then sends the user looking for a fault at the phone.
            Status = _manager.ConnectedDevices > 0
                ? Devices.Count == 1
                    ? $"{Devices[0].Label} is connected — hold the button on it and speak"
                    : $"{Devices.Count} devices connected"
                : Devices.Count == 1
                    ? $"{Devices[0].Label} is paired, but not connected right now"
                    : $"{Devices.Count} devices paired, none connected right now";
        }
        else if (_ready)
        {
            // Put back what it said before anything paired. Without this the panel went on announcing
            // "iPhone is connected" after the user had just pressed the button that disconnected it —
            // the one moment they are certainly looking at that line.
            Status = ReadyStatus();
        }
    }

    /// <summary>What the header says when the bridge is up and nothing is paired yet.</summary>
    private string ReadyStatus() =>
        Codes.Any(code => code.WarnsAboutCertificate)
            ? "Ready — a code marked below will ask your phone to accept a security warning"
            : "Ready — scan with your phone";

    /// <summary>
    /// Redraws as much as the change warrants.
    /// </summary>
    /// <remarks>
    /// A device pairing must not re-issue the codes — that would invalidate the one the user is holding
    /// their phone up to. But a bridge that has restarted underneath the panel (a network change, a port
    /// applied from Settings) leaves every code on screen addressed to a port nothing is listening on,
    /// and the panel had no way of noticing: it only ever refreshed the device list.
    /// </remarks>
    private void OnManagerChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;

        if (_manager.Generation != _drawnForGeneration)
        {
            Redraw();
            DescribeBridge();
            return;
        }

        RefreshDevices();
    });

    /// <summary>
    /// Which server the codes on screen were built for.
    /// </summary>
    /// <remarks>
    /// A counter rather than the port. A restart caused by a change of network almost always lands on the
    /// same port with a different set of addresses — which is exactly the case that leaves every code on
    /// screen addressed to something the server no longer answers for, and the one a port comparison
    /// cannot see.
    /// </remarks>
    private int _drawnForGeneration = -1;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _troubleTimer.Stop();
        _codeTimer.Stop();
        _manager.StateChanged -= OnManagerChanged;

        // Also here, not only in CloseAsync. Closing the panel is meant to withdraw what was on screen —
        // that is what makes it a way to revoke a code somebody may have photographed — and a teardown
        // that skipped CloseAsync left those codes redeemable for the rest of their two minutes.
        _manager.StopShowingCodes();

        // Belt and braces: CloseAsync normally releases it, but a panel torn down another way must not
        // leave the bridge pinned up for the rest of the session.
        var hold = _hold;
        _hold = null;
        hold?.Dispose();

        DisposeCodes();
    }
}
