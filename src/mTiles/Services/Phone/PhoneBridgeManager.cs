using System.Diagnostics;
using System.Net;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Speech;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// The phone bridge as one thing: which addresses to offer, who is paired, the server, and the route from
/// a phone's microphone into the tile the user is looking at.
/// </summary>
/// <remarks>
/// Modelled on <see cref="Database.DatabaseServiceManager"/> — one object the application starts and
/// stops, raising <see cref="StateChanged"/> for the UI to redraw from — because the two have the same
/// shape: a server whose lifetime is a user decision, with state worth showing while it runs.
/// <para>It implements <see cref="IPhoneAudioSink"/> rather than handing the server a pile of callbacks,
/// which keeps the transport ignorant of dictation and this class ignorant of WebSockets.</para>
/// </remarks>
public sealed class PhoneBridgeManager : IPhoneAudioSink, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly DictationService _dictation;
    private readonly RoutedAudioCapture _router;
    private readonly Func<LeafTileNodeViewModel?> _activeTile;
    private readonly PhoneEndpointDirectory _directory;
    private readonly PhoneCertificateProvider _certificates;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Which address to listen on. Null means every interface, which is the point of the
    /// feature; the tests pass loopback so running them raises no firewall prompt.</summary>
    private readonly IPAddress? _bindTo;

    private PhoneBridgeServer? _server;
    private PhoneTlsMaterial? _tls;

    /// <summary>
    /// Set once the manager has been disposed, so nothing queued outlives it.
    /// </summary>
    /// <remarks>
    /// Disposal releases the lifecycle semaphore, and a timer callback or a hold released afterwards
    /// would wait on it — an <see cref="ObjectDisposedException"/> on a thread-pool thread, from work
    /// nobody asked for, after the object it belonged to had gone.
    /// </remarks>
    private volatile bool _disposed;

    /// <summary>Serialises every start and stop. See <see cref="StartAsync"/> for what it prevents.</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    /// <summary>The addresses the running server was configured for. Empty when it is not running.</summary>
    private HashSet<string> _activeHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many panels are open. The bridge stays up while any of them is.</summary>
    private int _holds;

    private readonly Lock _reconfigureGate = new();
    private Timer? _reconfigure;

    /// <summary>Set by the network watcher, so an ordinary settings edit does not re-read the adapters.</summary>
    private volatile bool _addressesMayHaveChanged;

    /// <summary>
    /// The settings as last acted on, so an unrelated save does nothing.
    /// </summary>
    /// <remarks>
    /// Three values, not the one they combine into. <c>ShouldKeepRunning</c> is already false when the
    /// phone switch is off, so switching <em>dictation</em> off left it false either side of the change —
    /// the gate saw nothing move and scheduled nothing, while a bridge held up by a paired phone went on
    /// listening.
    /// </remarks>
    private bool _appliedPhoneEnabled;
    private bool _appliedSpeechEnabled;
    private int _appliedPort = -1;

    /// <summary>
    /// Bumped every time the server is (re)started, so a panel can tell it is looking at a different one.
    /// </summary>
    /// <remarks>
    /// The port alone does not say: a restart caused by a change of network usually lands on the *same*
    /// port with a different set of addresses, which is precisely the case that leaves every code on
    /// screen addressed to something the server no longer answers for.
    /// </remarks>
    internal int Generation { get; private set; }

    /// <summary>
    /// Drops sessions that have gone stale, and lets go of the network once the last one has.
    /// </summary>
    /// <remarks>
    /// Nothing else notices an expiry. A device that timed out went on counting as "a phone is paired",
    /// which is one of the two things that keep this listening — so with the setting off and the panel
    /// closed, one phone paired at breakfast held the socket open for the rest of the day. Five minutes
    /// is a dictionary scan against an eight-hour timeout: it costs nothing, and it is what makes
    /// "listens only while the panel is open or a phone is paired" true rather than nearly true.
    /// </remarks>
    private Timer? _sweep;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>The port asked for when the running server was started. Compared against the setting
    /// to decide whether a restart is due — <see cref="ActivePort"/> cannot be, because a fallback makes
    /// the two differ on purpose and comparing it would restart the bridge for ever.</summary>
    private int _requestedPort = -1;

    /// <summary>The tile this stream was aimed at when the user pressed, held for the transcript's
    /// arrival. Touched only on the UI thread.</summary>
    private LeafTileNodeViewModel? _streamTile;

    /// <summary>
    /// The name of the tile the phone is aimed at, as last read on the UI thread.
    /// </summary>
    /// <remarks>
    /// Cached rather than read on demand, because the only caller is a socket thread and the value lives
    /// in an Avalonia view model tree — the one place in this class that reached into the UI graph from
    /// the network. Nothing observable went wrong yet, which is exactly why it is worth removing now
    /// rather than after it does: what a stale name costs is a wrong caption for a fraction of a second,
    /// and what a torn read costs is not bounded at all.
    /// </remarks>
    private volatile string _tileName = "";

    internal PhoneBridgeManager(
        SettingsService settings,
        DictationService dictation,
        RoutedAudioCapture router,
        Func<LeafTileNodeViewModel?> activeTile,
        PhoneEndpointDirectory? directory = null,
        PhoneCertificateProvider? certificates = null,
        IUiDispatcher? dispatcher = null,
        IPAddress? bindTo = null,
        IPhoneSessionStore? sessionStore = null)
    {
        _bindTo = bindTo;
        Pairing = new PhonePairing(store: sessionStore ?? new PhoneSessionStore());
        _settings = settings;
        _dictation = dictation;
        _router = router;
        _activeTile = activeTile;
        _directory = directory ?? PhoneEndpointDirectory.CreateDefault();
        _certificates = certificates ?? PhoneCertificateProvider.CreateDefault();
        _dispatcher = dispatcher ?? new AvaloniaUiDispatcher();

        // Posted, not raised here. StopCoreAsync revokes the pairings while holding the lifecycle
        // semaphore, so this fired under it — the one remaining path doing what the notes on StartAsync
        // and StopIfUnneededAsync both warn against, in this same file. It works today only because the
        // single subscriber posts to the dispatcher itself, which is a property of the subscriber and not
        // something this class can rely on.
        // Seeded from what is on disk, so the first unrelated save does not read as a change and
        // schedule a reconfiguration of something that is already in that state.
        MarkApplied();

        Pairing.Changed += () => _dispatcher.Post(() => StateChanged?.Invoke());
        _dictation.StateChanged += PublishState;
        _settings.SettingsChanged += OnSettingsChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    /// <summary>Raised when the bridge starts or stops, or a device pairs or leaves.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// What has the keyboard right now, wired from the window.
    /// </summary>
    /// <remarks>
    /// The panel promises that dictating from a phone works "exactly as the Alt+Space shortcut does", and
    /// the shortcut hands its transcript to the focused text control before falling back to the tile's
    /// terminal. Without this the phone had no such fallback, so speaking while a Note or a settings box
    /// had focus produced a transcript with nowhere to go: the active tile was not a terminal, delivery
    /// failed, and the words were reported undeliverable rather than typed. Resolved on the UI thread
    /// when the recording starts — the same moment the shortcut resolves it, and for the same reason: the
    /// text belongs where the user was looking when they spoke.
    /// </remarks>
    internal Func<Avalonia.Input.IInputElement?>? FocusedElement { get; set; }

    internal PhonePairing Pairing { get; }

    /// <summary>The settings this bridge reads, so a view that has the bridge need not also be handed them.</summary>
    public SettingsService Settings => _settings;

    internal IFirewallGuide Firewall { get; } = FirewallGuide.ForThisMachine();

    public bool IsRunning => _server is { IsRunning: true };

    /// <summary>Why the last start failed, for the panel to show. Null when it did not.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Whether a phone reaching this machine at <paramref name="host"/> sees no certificate warning.
    /// </summary>
    /// <remarks>
    /// Per host, not per bridge. With Tailscale running, one of the two QR codes on screen leads to a
    /// publicly-trusted certificate and the other cannot — so a single flag was wrong for one of them
    /// whichever value it took, and it was wrong in the direction that surprises the user.
    /// </remarks>
    internal bool IsTrustedFor(string host) => _tls?.IsTrustedFor(host) ?? false;

    /// <summary>The addresses on offer, as of the last <see cref="RefreshAsync"/>.</summary>
    internal PhoneEndpointBoard Board { get; private set; } = PhoneEndpointBoard.Empty;

    internal SessionLocation Location => _directory.Location;

    /// <summary>The port the user asked for. Zero means "whichever one is free".</summary>
    public int Port => _settings.Settings.Phone.Port;

    /// <summary>
    /// Whether the bridge should stay up of its own accord.
    /// </summary>
    /// <remarks>
    /// Both switches, not just the phone one. The QR button is hidden when dictation is off — there would
    /// be nothing for a phone to do — but the start condition only ever read <c>Phone.Enabled</c>, so
    /// somebody who turned dictation off and had left "keep running" on was left with a server listening
    /// on the network, unreachable from the application and useless if reached. The two conditions have to
    /// be the same one.
    /// </remarks>
    internal bool ShouldKeepRunning =>
        _settings.Settings.Phone.Enabled && _settings.Settings.Speech.Enabled;

    /// <summary>
    /// The port the bridge is really on, which is what the QR codes point at.
    /// </summary>
    /// <remarks>
    /// Not always <see cref="Port"/>. On Windows the kernel reserves blocks of ports for Hyper-V, WSL and
    /// Docker at boot — <c>netsh interface ipv4 show excludedportrange protocol=tcp</c> lists them — and a
    /// port inside one cannot be bound by anything, ever, however free it looks. It is not even a
    /// collision with another program: <c>netstat</c> attributes it to PID 4, the kernel. A fixed default
    /// port is therefore a coin toss on a developer machine, which is exactly what this application runs
    /// on: 18091 was unbindable on the first machine it was tried on.
    /// </remarks>
    public int ActivePort => _server?.BoundPort ?? 0;

    /// <summary>True when the bridge had to fall back from the port the user asked for.</summary>
    public bool PortWasSubstituted => IsRunning && Port != 0 && ActivePort != Port;

    internal IReadOnlyList<PhoneSession> Sessions => Pairing.Sessions;

    /// <summary>How many paired devices have a socket open right now. See <c>PhoneBridgeServer</c>.</summary>
    internal int ConnectedDevices => _server?.ConnectedCount ?? 0;

    /// <summary>Re-discovers and re-ranks the addresses without touching the server.</summary>
    /// <remarks>
    /// Off the UI thread because it enumerates adapters and may shell out to Tailscale — a second or two
    /// on a machine with a VPN client that is thinking about something else.
    /// </remarks>
    internal async Task RefreshAsync()
    {
        await RefreshCoreAsync().ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The same work without the event, for callers that already hold the lifecycle lock.
    /// </summary>
    /// <remarks>
    /// <see cref="StartCoreAsync"/> refreshes when the board is empty, and it runs under the semaphore —
    /// so raising from inside was the very thing the note on <see cref="StartAsync"/> warns against, in
    /// the file that states the rule. A handler free to ask this object to start or stop would have
    /// deadlocked on a lock that is not reentrant.
    /// </remarks>
    private async Task RefreshCoreAsync() =>
        Board = await Task.Run(() => _directory.Build(PinnedHostFor)).ConfigureAwait(false);

    /// <summary>
    /// Starts listening, or reconfigures a running bridge when the port or the address set has changed.
    /// Returns false and sets <see cref="LastError"/> on failure.
    /// </summary>
    /// <remarks>
    /// <b>Serialised, and that is not defensive tidiness.</b> Three callers reach here without
    /// coordinating: the panel opening, the application starting with the setting on, and any settings
    /// change — which includes the one this class makes itself when it pins the address a phone arrived
    /// on, <em>during that phone own pairing request</em>. Two overlapping starts both saw no running
    /// server, both built one, and the second failed to bind — whereupon its own error path called
    /// StopAsync and disposed the <em>first one</em> server. The user was told "port already in use",
    /// naming a port that nothing but this application was using, and left with a bridge that had just
    /// stopped listening.
    /// </remarks>
    internal async Task<bool> StartAsync()
    {
        if (_disposed)
            return false;

        bool started;

        // The flag is checked before the wait and the wait can still lose the race with disposal, so the
        // throw is caught rather than left to become an unobserved exception on a thread-pool thread.
        try { await _lifecycle.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }

        try
        {
            started = await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        // Outside the lock. A handler is free to ask this object to start or stop, and the semaphore is
        // not reentrant — raising the event while holding it made that a deadlock rather than a mistake.
        // Nothing does today, which is luck, not design.
        StateChanged?.Invoke();
        return started;
    }

    private async Task<bool> StartCoreAsync()
    {
        LastError = null;

        // Held so the failure path can release them. `_server` and `_tls` are only assigned once the
        // socket is listening, so before that point nothing else in this class can reach either one.
        PhoneBridgeServer? scratchServer = null;
        PhoneTlsMaterial? scratchTls = null;

        try
        {
            if (Board.All.Count == 0)
                await RefreshCoreAsync().ConfigureAwait(false);

            var hosts = Board.All.Select(entry => entry.Endpoint.Host).Distinct().ToList();
            if (hosts.Count == 0)
            {
                LastError = "This machine has no network address a phone could reach.";
                return false;
            }

            if (IsRunning && !NeedsRestart(hosts))
                return true;

            if (IsRunning)
            {
                // A reconfiguration, not a shutdown: paired phones keep their sessions and reconnect with
                // the cookie they already hold. Revoking here would mean that connecting a VPN — which
                // only ever *adds* a way to reach this machine — silently unpaired the phone in the
                // user hand.
                await StopCoreAsync(revokePairings: false).ConfigureAwait(false);
            }

            var tls = await Task.Run(() => _certificates.Resolve(hosts)).ConfigureAwait(false);
            if (tls is not { Any: true })
            {
                LastError = "No TLS certificate could be obtained, and a phone will not open a "
                            + "microphone on a page that is not secure.";
                return false;
            }

            scratchTls = tls;

            var server = new PhoneBridgeServer(Pairing, this, RememberReachedVia);
            server.ConnectionsChanged += OnConnectionsChanged;
            scratchServer = server;
            var wanted = ClampPort(Port);

            try
            {
                await server.StartAsync(wanted, tls, hosts, _bindTo).ConfigureAwait(false);
            }
            catch (Exception ex) when (wanted != 0 && IsPortUnavailable(ex))
            {
                // Falls back rather than refusing. The port is an implementation detail nobody types
                // anywhere — the QR code carries it — so failing the whole feature to defend a number the
                // user never chose deliberately is the wrong trade. Which port was taken is shown in the
                // panel, so it is a visible fallback and not a silent one.
                Trace.TraceInformation(
                    "Port {0} could not be bound ({1}); taking a free one instead.", wanted, ex.Message);

                await server.DisposeAsync().ConfigureAwait(false);
                server = new PhoneBridgeServer(Pairing, this, RememberReachedVia);
                server.ConnectionsChanged += OnConnectionsChanged;
                scratchServer = server;
                await server.StartAsync(0, tls, hosts, _bindTo).ConfigureAwait(false);
            }

            _server = server;
            _tls = tls;

            // The setting as written, not the number that was tried: a clamped or substituted value must
            // not make NeedsRestart disagree with the setting for ever after.
            _requestedPort = Port;

            // Handed over. Nothing is scratch any more, so the failure path must not release it.
            scratchServer = null;
            scratchTls = null;
            _activeHosts = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);

            _sweep ??= new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
            Generation++;

            return true;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            Trace.TraceWarning("The phone bridge could not start: {0}", ex);

            // Whatever got built on the way to failing. Without this, every failed start leaked a server
            // — with its Kestrel host and its subscription to PhonePairing — and a certificate holding an
            // operating-system key handle.
            if (scratchServer is not null)
                await scratchServer.DisposeAsync().ConfigureAwait(false);

            scratchTls?.Dispose();

            // The devices are left alone entirely — not merely left on disk. Keeping the file while
            // clearing the list in memory was half a fix: the phone stayed paired according to the file
            // and unpaired according to the process, so it could not reconnect until mTiles was
            // restarted, which is not what the comment claiming to protect it promised. There is no
            // server at this point, so a live session can do nothing anyway; and a start that fails for
            // reasons having nothing to do with the user's phones — no address yet on a machine that has
            // just woken, a certificate that could not be written — has no business touching them.
            await StopCoreAsync(revokePairings: false).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Whether the running server is still serving the right thing.
    /// </summary>
    /// <remarks>
    /// <b>The address set, not only the port.</b> The server fixes its allowed <c>Host</c> values and its
    /// certificate names when it starts, so a bridge left running across a change of network — which is
    /// exactly what "keep running" invites, on a laptop — kept answering for addresses this machine no
    /// longer has and rejecting the one it now does. The panel would then draw a perfectly good QR code
    /// for the new address, and the phone that scanned it met a bare <c>400</c>: no page, no explanation,
    /// and nothing in the panel suggesting anything was wrong.
    /// </remarks>
    private bool NeedsRestart(IReadOnlyCollection<string> hosts) =>
        _requestedPort != Port || !_activeHosts.SetEquals(hosts);

    /// <summary>
    /// Keeps a hand-edited port inside the range a socket can be asked for.
    /// </summary>
    /// <remarks>
    /// <c>settings.json</c> is a file the user can open, and <c>Listen</c> answers an out-of-range number
    /// with <see cref="ArgumentOutOfRangeException"/> — which is not a bind failure, so it does not take
    /// the fallback and the feature is simply dead with an obscure message. Out of range is treated as
    /// "choose one for me", which is what the fallback does for every other unusable value.
    /// </remarks>
    internal static int ClampPort(int port) => port is >= 0 and <= 65535 ? port : 0;

    /// <summary>
    /// Whether this is "that port is not available", as opposed to something worth reporting.
    /// </summary>
    /// <remarks>
    /// Three shapes turn up, and assuming one was a bug the tests caught. Kestrel reports a plain
    /// collision as <see cref="Microsoft.AspNetCore.Connections.AddressInUseException"/> — which derives
    /// from <see cref="InvalidOperationException"/>, not from anything network-shaped, so matching on
    /// <see cref="System.Net.Sockets.SocketException"/> alone silently missed the commonest case of all.
    /// A port inside a kernel-reserved range comes back as an <see cref="IOException"/> wrapping a socket
    /// error instead. Anything else — a bad certificate, no permission to listen at all — must not be
    /// quietly retried on a different port: the retry fails the same way and buries the real reason.
    /// </remarks>
    private static bool IsPortUnavailable(Exception ex) =>
        IsBindFailure(ex) || IsBindFailure(ex.InnerException);

    private static bool IsBindFailure(Exception? ex) =>
        ex is System.Net.Sockets.SocketException or Microsoft.AspNetCore.Connections.AddressInUseException;

    /// <summary>Stops listening and drops every pairing.</summary>
    internal async Task StopAsync()
    {
        if (_disposed)
            return;

        try { await _lifecycle.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        try
        {
            await StopCoreAsync(revokePairings: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        StateChanged?.Invoke();
    }

    private async Task StopCoreAsync(bool revokePairings, bool forgetDevices = true)
    {
        var server = _server;
        _server = null;
        _activeHosts.Clear();
        _requestedPort = -1;

        if (server is not null)
            server.ConnectionsChanged -= OnConnectionsChanged;

        _sweep?.Dispose();
        _sweep = null;

        if (server is not null)
            await server.DisposeAsync().ConfigureAwait(false);

        // Certificates hold key handles — on Windows, ones the operating system keeps until they are
        // released. Restarting the bridge for a port change is an ordinary act, so leaking a pair of
        // them each time is not.
        _tls?.Dispose();
        _tls = null;

        if (revokePairings)
            Pairing.RevokeAll(forgetDevices);
    }

    /// <summary>
    /// Keeps the bridge up while a panel is on screen, whatever the setting says.
    /// </summary>
    /// <remarks>
    /// A scope rather than a flag on the view model, so the rule for "may this stop now" lives in one
    /// place instead of being restated by every caller that might want it stopped — the panel closing,
    /// the setting being switched off, the last phone being disconnected.
    /// </remarks>
    internal IDisposable HoldOpen()
    {
        Interlocked.Increment(ref _holds);
        return new Hold(this);
    }

    /// <summary>
    /// Stops the bridge if nothing needs it: not asked to stay up, no panel open, no phone paired.
    /// </summary>
    internal async Task StopIfUnneededAsync()
    {
        if (_disposed)
            return;

        var stopped = false;

        // Under the same lock as starting, and re-checked inside it: the conditions are all things a
        // concurrent start is in the middle of changing.
        try { await _lifecycle.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (!IsRunning)
                return;

            if (ShouldKeepRunning)
                return;

            // The exemption applies only while dictation is on. It exists so that closing the panel does
            // not cut off a phone somebody is using — but with dictation switched off there is nothing
            // for that phone to do, and the QR button is hidden, so the exemption kept a socket listening
            // on the network *and* removed the only control able to close it. It would have stayed up
            // until the session idled out eight hours later, or the application was closed.
            // SessionCount, not Sessions: the latter sweeps and raises Changed, and this runs under the
            // lifecycle lock — the very thing the notes on StartAsync warn about.
            if (_settings.Settings.Speech.Enabled &&
                (Volatile.Read(ref _holds) > 0 || Pairing.SessionCount > 0))
                return;

            await StopCoreAsync(revokePairings: true).ConfigureAwait(false);
            stopped = true;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (stopped)
            StateChanged?.Invoke();
    }

    /// <summary>
    /// Reacts to the switch and the port in Settings.
    /// </summary>
    /// <remarks>
    /// <para>Without this the switch was write-only: turning it <em>off</em> left a server listening on
    /// the network until the application was restarted, which is the wrong direction for the one setting
    /// whose whole purpose is to stop that. Turning it on starts the bridge, so a paired phone can
    /// reconnect without the panel being opened first — which is what the setting promises.</para>
    /// <para>Gated on the two values it can act on. This listens to the whole settings file, so without
    /// the gate every keystroke in any settings box scheduled a reconfiguration — and a reconfiguration
    /// re-reads the machine's addresses, which shells out to <c>tailscale status</c>. Typing a font name
    /// was spawning processes.</para>
    /// </remarks>
    private void OnSettingsChanged()
    {
        var settings = _settings.Settings;

        if (settings.Phone.Enabled == _appliedPhoneEnabled &&
            settings.Speech.Enabled == _appliedSpeechEnabled &&
            settings.Phone.Port == _appliedPort)
            return;

        ScheduleReconfigure();
    }

    /// <summary>
    /// Re-discovers and reconfigures when this machine's addresses change underneath a running bridge.
    /// </summary>
    /// <remarks>
    /// Without it, "keep running" came apart on a laptop. The address set is only re-read when the panel
    /// is opened, and the whole point of that setting is that the panel never has to be — so a machine
    /// that joined another network kept a server configured for the old one: answering for addresses it
    /// no longer has, holding a certificate that does not name the one it does, and rejecting the paired
    /// phone that tried to come back. Nothing on screen would have said so, because nothing was on screen.
    /// <para>Shares the debounce with Settings: this event arrives in bursts — several times for a single
    /// Wi-Fi handover, and once per adapter — and each one would otherwise be a rebind.</para>
    /// </remarks>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Not "only while running". A start that failed for want of an address is exactly the state a
        // network change should rescue, and it is the ordinary one: a laptop resumes, mTiles is up before
        // the Wi-Fi has associated, the start finds nothing to bind to — and then this event arrived and
        // was thrown away, leaving the bridge down until the application was restarted or somebody opened
        // the panel by hand.
        if (!IsRunning && !ShouldKeepRunning)
            return;

        _addressesMayHaveChanged = true;
        ScheduleReconfigure();
    }

    /// <summary>A device connected or disconnected; the panel's "paired versus connected" depends on it.</summary>
    private void OnConnectionsChanged() => _dispatcher.Post(() => StateChanged?.Invoke());

    /// <summary>How long a displayed pairing code stays redeemable.</summary>
    internal static TimeSpan CodeLifetime => PhonePairing.DefaultPairingLifetime;

    /// <summary>Wrapped, because this runs on a thread-pool timer and an escape ends the process.</summary>
    private void Sweep()
    {
        try
        {
            Pairing.Sweep();
            _ = StopIfUnneededAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Sweeping paired phones failed: {0}", ex);
        }
    }

    private void ScheduleReconfigure()
    {
        lock (_reconfigureGate)
        {
            _reconfigure?.Dispose();
            _reconfigure = new Timer(
                _ => _ = ApplySettingsAsync(), null, ReconfigureDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// How long to let the settings settle before acting on them.
    /// </summary>
    /// <remarks>
    /// The port is a spinner bound straight to the stored value, so raising it from 18091 to 18095 saves
    /// five times on the way. Without this, each of those intermediate numbers tore the server down and
    /// bound it again — four pointless rebinds, four chances to lose a race with the operating system
    /// over a port still in TIME_WAIT, and a paired phone dropped in the middle. It also covers the burst
    /// of unrelated saves that any settings edit produces, since this listens to the whole file.
    /// </remarks>
    private const int ReconfigureDelayMs = 750;

    private async Task ApplySettingsAsync()
    {
        // Wrapped, because this runs on a thread-pool thread from a timer: an exception escaping here
        // ends the process, and no reconfiguration is worth the application.
        try
        {
            // Before the branch, not inside one of them. StartAsync compares the running configuration
            // against whatever Board last said, so a stale Board hides a real change — and the branch this
            // used to sit in was the wrong one: a network change while dictation is switched off but the
            // panel is open took the *other* path, reached StartAsync with yesterday's addresses, decided
            // nothing had changed, and dropped the event on the floor.
            //
            // Only when the network said so, because re-reading costs a process spawn for `tailscale
            // status` and an ordinary settings edit has no reason to pay it.
            if (IsRunning && _addressesMayHaveChanged)
            {
                _addressesMayHaveChanged = false;
                await RefreshAsync().ConfigureAwait(false);
            }

            if (!ShouldKeepRunning)
            {
                await StopIfUnneededAsync().ConfigureAwait(false);

                // Still up, because a panel is open or a phone is paired. A changed port or a changed
                // address set has to reach it anyway: marking the settings applied while the server kept
                // the old ones meant the gate never fired for those values again.
                if (IsRunning && !await StartAsync().ConfigureAwait(false))
                    return;

                MarkApplied();
                return;
            }

            // covers a changed port or a changed address set
            if (await StartAsync().ConfigureAwait(false))
                MarkApplied();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Applying the phone settings failed: {0}", ex);
        }
    }

    /// <summary>
    /// Records the settings as acted on, so an unrelated save does not schedule another attempt.
    /// </summary>
    /// <remarks>
    /// Only after success. Doing it in a <c>finally</c> marked a <em>failed</em> reconfiguration as
    /// applied, and the gate in <see cref="OnSettingsChanged"/> then refused to try again for the same
    /// values — so switching "keep running" on while the bridge could not start meant it stayed off until
    /// the application was restarted, with the switch showing on.
    /// </remarks>
    private void MarkApplied()
    {
        var settings = _settings.Settings;
        _appliedPhoneEnabled = settings.Phone.Enabled;
        _appliedSpeechEnabled = settings.Speech.Enabled;
        _appliedPort = settings.Phone.Port;
    }

    private sealed class Hold(PhoneBridgeManager owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            Interlocked.Decrement(ref owner._holds);
            _ = owner.StopIfUnneededAsync();
        }
    }

    /// <summary>
    /// The URL to put in a QR code for <paramref name="endpoint"/>, minting a fresh pairing token.
    /// </summary>
    /// <remarks>
    /// Every displayed code gets its own token and all of them stay live, because the panel shows two and
    /// the second exists precisely for when the first does not work.
    /// </remarks>
    internal (string Url, string Token) BuildPairingUrl(PhoneEndpoint endpoint)
    {
        var token = Pairing.IssuePairingToken();
        return ($"https://{endpoint.Host}:{ActivePort}/p/{token}", token);
    }

    /// <summary>Withdraws the displayed codes. Called when the panel closes.</summary>
    internal void StopShowingCodes() => Pairing.ClearPairingTokens();

    /// <summary>Opens the firewall for the bridge's port, prompting for the rights to do it.</summary>
    internal Task<FirewallResult> RepairFirewallAsync() => Firewall.TryAllowAsync(ActivePort);

    // ── IPhoneAudioSink ─────────────────────────────────────────────────────────────────────────────

    Task<PhoneStreamOutcome> IPhoneAudioSink.BeginAsync(int sampleRate) =>
        _dispatcher.InvokeAsync(() =>
        {
            // Each refusal names its own cause. The phone is often the only screen the user is looking
            // at, and "it did not work" sends them back to the computer to guess which of four things
            // was wrong.
            if (!_settings.Settings.Speech.Enabled)
                return new PhoneStreamOutcome(false, "Dictation is switched off in mTiles.");

            // Deliberately *not* DictationService.IsReady. That asks, among other things, whether this
            // machine has a working audio backend — and it is answered before the phone route is armed,
            // so on a machine with no microphone at all it says no. Those are precisely the machines this
            // feature exists for: the far end of a remote desktop session. Worse, the refusal blamed a
            // missing model, sending the user to download something that was already there.
            if (_dictation.SelectedModel is not { } model || !_dictation.Store.IsDownloaded(model))
                return new PhoneStreamOutcome(false,
                    "mTiles has no speech model downloaded yet. Set up dictation on the computer first.");

            if (_dictation.State == DictationState.Transcribing)
                return new PhoneStreamOutcome(false, "mTiles is still working out the previous recording.");

            if (_dictation.State != DictationState.Idle)
                return new PhoneStreamOutcome(false, "mTiles is already recording.");

            var tile = _activeTile();
            var focused = SafeFocusedElement();
            _streamTile = tile;
            _tileName = SafeTileName();

            var forPhone = PhoneDelivery(_settings.Settings);

            bool started;
            try
            {
                // Prepared before the route is armed and before the service is asked to start, because
                // audio is already on its way: the phone sends "begin" and then frames, without waiting
                // for a reply. Inside the try, because this throws too — a disposed capture, a rate the
                // resampler will not take — and an escape from here reaches Kestrel as an unhandled
                // exception instead of the phone as a refusal.
                _router.Phone.PrepareForStream(sampleRate);
                _router.RouteNextToPhone();

                started = _dictation.Start(tile ?? (object)"phone",
                text =>
                {
                    // To the phone that spoke, and only that one. Broadcasting it put one person's
                    // dictation on every paired device's screen — a second phone in the room, or the
                    // browser left open on the near machine, would have shown it. It is shown at all
                    // because whoever is holding the phone is usually not looking at the computer, and
                    // "nothing happened" is otherwise indistinguishable from "it did not hear you".
                    _server?.SendToStreamOwner(
                        JsonSerializer.Serialize(new { type = "text", message = text }));
                    return DictationTextSink.Insert(tile, text, forPhone, focused);
                });

            }
            catch (Exception ex)
            {
                // The armed route is the thing that must not survive this. It is a one-shot flag that the
                // next Start consumes, so leaving it set sends the user's *own* microphone press to a
                // phone capture with nothing in it — local dictation quietly broken until somebody
                // happens to dictate from a phone again.
                _router.CancelPhoneRoute();
                _streamTile = null;
                Trace.TraceWarning("Starting a phone dictation failed: {0}", ex);
                return new PhoneStreamOutcome(false, "Dictation could not be started on the computer.");
            }

            if (started)
                return PhoneStreamOutcome.Ok;

            _router.CancelPhoneRoute();
            _streamTile = null;
            return new PhoneStreamOutcome(false, "Dictation could not be started on the computer.");
        });

    void IPhoneAudioSink.Write(ReadOnlySpan<byte> pcm) => _router.Phone.Write(pcm);

    Task IPhoneAudioSink.EndAsync()
    {
        _dispatcher.Post(() =>
        {
            if (_dictation.State == DictationState.Recording && _router.IsRecordingFromPhone)
                _dictation.Stop();
        });
        return Task.CompletedTask;
    }

    void IPhoneAudioSink.CancelStream() => _dispatcher.Post(() =>
    {
        if (_dictation.State == DictationState.Recording && _router.IsRecordingFromPhone)
            _dictation.Cancel();

        _router.CancelPhoneRoute();
        _streamTile = null;
    });

    string IPhoneAudioSink.DescribeState()
    {
        // Answers immediately from the cache — this is a socket thread and the phone is waiting — and
        // asks for a fresh reading, which arrives a frame later as an ordinary state message.
        PublishState();
        return StateJson();
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The settings a phone-driven transcript is composed with.
    /// </summary>
    /// <remarks>
    /// A copy carrying only what <see cref="DictationTextSink.Compose"/> reads, so the phone's own
    /// auto-Enter preference applies without the stored keyboard settings being mutated — and so nothing
    /// else about the local configuration can leak into a path it was never set for.
    /// </remarks>
    internal static SpeechSettings PhoneDelivery(AppSettings settings) => new()
    {
        AutoSubmitEnter = settings.Phone.AutoSubmitEnter,
        AppendTrailingSpace = settings.Speech.AppendTrailingSpace,
    };

    /// <summary>Reads the tile name on the UI thread and tells every phone where it stands.</summary>
    private void PublishState() => _dispatcher.Post(() =>
    {
        // Let go of the tile once the utterance is over. Holding it meant the phone kept naming the tile
        // it had dictated into an hour ago, however many times the user had switched since — and the
        // whole point of showing a name there is to say where the next thing you say will land.
        if (_dictation.State == DictationState.Idle)
            _streamTile = null;

        _tileName = SafeTileName();
        _server?.Broadcast(StateJson());
    });

    private Avalonia.Input.IInputElement? SafeFocusedElement()
    {
        try { return FocusedElement?.Invoke(); }
        catch { return null; }
    }

    private string SafeTileName()
    {
        try { return (_streamTile ?? _activeTile())?.TileName ?? ""; }
        catch { return ""; }
    }

    private string StateJson()
    {
        var state = _dictation.State switch
        {
            DictationState.Recording => "recording",
            DictationState.Transcribing => "transcribing",
            _ => "idle",
        };

        return JsonSerializer.Serialize(new { type = "state", state, tile = _tileName });
    }

    /// <summary>
    /// The pinned address for a kind of session. Safe from any thread: the dictionary it reads is only
    /// ever replaced, never written into.
    /// </summary>
    private string? PinnedHostFor(SessionLocation location) =>
        _settings.Settings.Phone.PinnedHosts.GetValueOrDefault(location.ToString());

    /// <summary>
    /// Records the address a phone genuinely reached this machine at, under the current kind of session.
    /// </summary>
    /// <remarks>
    /// The one fact in this whole feature that is measured rather than inferred, which is why it outranks
    /// every heuristic in <see cref="PhoneEndpointRanker"/>. Stored per session location so that a local
    /// day and a remote day do not overwrite each other's answer — the machine is the same in both, and a
    /// single remembered winner would be wrong every time the user switched.
    /// </remarks>
    private void RememberReachedVia(string host)
    {
        var key = _directory.Location.ToString();

        // Posted to the UI thread, and that is not tidiness. This runs on a Kestrel request thread while
        // the settings graph is a plain Dictionary that the debounced save serialises from elsewhere:
        // writing a key during that walk throws InvalidOperationException inside the save, on a
        // thread-pool thread, at a moment nobody would connect to a phone having scanned a QR code.
        // Every other writer of this file is already on the UI thread; this one joins them.
        _dispatcher.Post(() =>
        {
            var pins = _settings.Settings.Phone.PinnedHosts;

            if (pins.TryGetValue(key, out var existing) &&
                string.Equals(existing, host, StringComparison.OrdinalIgnoreCase))
                return;

            // Replaced, not mutated. A lock here only covered this class's own two accesses — and the
            // third reader is the debounced settings save, which serialises this dictionary from a
            // thread-pool thread and knows nothing about any lock of ours. Writing a new instance means
            // whoever is already walking the old one keeps walking something nobody will touch again.
            _settings.Settings.Phone.PinnedHosts = new Dictionary<string, string>(pins, StringComparer.Ordinal)
            {
                [key] = host,
            };

            _settings.NotifyChanged();
            StateChanged?.Invoke();
        });
    }

    private string Describe(Exception ex) => ex switch
    {
        // Reached only when even an automatically chosen port could not be bound, since a refused one is
        // retried. That is a machine-wide problem, not a number the user got wrong.
        _ when IsPortUnavailable(ex) =>
            "No port could be opened for the bridge. Something on this machine is blocking it.",
        _ => ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        _dictation.StateChanged -= PublishState;
        _settings.SettingsChanged -= OnSettingsChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;

        lock (_reconfigureGate)
        {
            _reconfigure?.Dispose();
            _reconfigure = null;
        }

        // Not StopAsync: that means "the user turned this off", and forgets the paired devices. Closing
        // the application is not that decision — treating it as one would have made the stored sessions
        // pointless, since every run would erase what the last one wrote.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(revokePairings: true, forgetDevices: false).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        _lifecycle.Dispose();
    }
}
