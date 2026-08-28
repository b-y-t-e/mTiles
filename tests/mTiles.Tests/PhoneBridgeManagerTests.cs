using System.Net;
using mTiles.Models;
using mTiles.Services.Phone;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The bridge's lifecycle: starting it, reconfiguring it, and knowing when it may stop.
/// </summary>
/// <remarks>
/// Everything here was reachable by ordinary use and invisible in a screenshot — two starts racing, a
/// laptop changing network, a panel closed while a phone is still paired. The constructor already takes
/// its address discovery, its certificates and its dispatcher, so all of it runs against loopback with a
/// certificate generated into a temporary directory and no UI thread anywhere.
/// </remarks>
public sealed class PhoneBridgeManagerTests : IDisposable
{
    private readonly string _certificateDirectory =
        Path.Combine(Path.GetTempPath(), "mtiles-bridge-tests-" + Guid.NewGuid().ToString("N"));

    private readonly TempSettings _settings = new();
    private readonly List<PhoneBridgeManager> _built = [];

    public void Dispose()
    {
        foreach (var manager in _built)
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _settings.Dispose();
        try { Directory.Delete(_certificateDirectory, true); } catch { }
    }

    // ── the world the manager runs in ───────────────────────────────────────────────────────────────

    /// <summary>Hands back whatever the test says this machine's addresses are, and can change its mind.</summary>
    private sealed class StubEndpoints : IPhoneEndpointSource
    {
        public List<string> Hosts { get; } = ["127.0.0.1"];

        public string Name => "stub";

        public IReadOnlyList<PhoneEndpoint> Discover() =>
            [.. Hosts.Select(host => new PhoneEndpoint(host, PhoneEndpointKind.Lan, "test", "test", true, false))];
    }

    private sealed class StubLocation : ISessionLocationProbe
    {
        public SessionLocation Current => SessionLocation.Console;
    }

    /// <summary>Runs the work inline. There is no UI thread here and nothing needs one.</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
    }

    private PhoneBridgeManager Build(StubEndpoints endpoints) => Build(endpoints, IPAddress.Loopback, null);

    private PhoneBridgeManager Build(StubEndpoints endpoints, IPAddress bindTo, IPhoneSessionStore? store)
    {
        // A real dictation service, given a capture that opens nothing. It is never asked to record here;
        // what matters is that the manager holds the same object the application gives it.
        var router = new RoutedAudioCapture(new SilentCapture(), new PhoneAudioCapture());
        var dictation = new DictationService(_settings.Service, router);

        var manager = new PhoneBridgeManager(
            _settings.Service,
            dictation,
            router,
            activeTile: () => null,
            directory: new PhoneEndpointDirectory([endpoints], new StubLocation()),
            certificates: new PhoneCertificateProvider([new SelfSignedCertificateSource(_certificateDirectory)]),
            dispatcher: new InlineDispatcher(),
            bindTo: bindTo,
            sessionStore: store ?? new NowhereStore());

        _built.Add(manager);
        return manager;
    }

    private void UsePort(int port) => _settings.Service.Settings.Phone.Port = port;

    /// <summary>
    /// "Choose one for me" — what every test here that does not care which port it gets asks for.
    /// </summary>
    /// <remarks>
    /// These used to open a listener, read the port it was given, close it, and configure that — which is
    /// a race with every other process on the machine, and one this suite lost: a run failed on a port
    /// something else had taken in between and passed on the next. Port zero has no window to lose,
    /// because the bind and the choice are the same act. A test that genuinely needs a port to be taken
    /// holds the listener open for as long as it needs the port to stay taken, which is the only way to
    /// mean it.
    /// </remarks>
    private void UseAnyPort() => UsePort(0);

    // ── starting ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_starts_and_stops()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());

        Assert.True(await bridge.StartAsync());
        Assert.True(bridge.IsRunning);

        await bridge.StopAsync();
        Assert.False(bridge.IsRunning);
    }

    /// <summary>
    /// The blocker. Three callers reach StartAsync without coordinating — including this class itself,
    /// which saves the pinned address during a phone's own pairing request. Both used to see no running
    /// server, both built one, and the loser's error path disposed the winner's: "port already in use"
    /// naming a port nothing else was using, and a bridge that had stopped listening.
    /// </summary>
    [Fact]
    public async Task Concurrent_starts_do_not_dismantle_each_other()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => bridge.StartAsync()));

        Assert.All(results, Assert.True);
        Assert.True(bridge.IsRunning);
        Assert.Null(bridge.LastError);
    }

    [Fact]
    public async Task Starting_an_already_running_bridge_changes_nothing()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());
        await bridge.StartAsync();

        Assert.True(await bridge.StartAsync());
        Assert.True(bridge.IsRunning);
    }

    /// <summary>
    /// A port that cannot be bound must not cost the user the feature.
    /// </summary>
    /// <remarks>
    /// This is not a rare collision. On Windows the kernel reserves blocks of ports for Hyper-V, WSL and
    /// Docker at boot — <c>netsh interface ipv4 show excludedportrange protocol=tcp</c> — and a port
    /// inside one can never be bound however free it looks; <c>netstat</c> attributes it to PID 4. The
    /// default 18091 landed inside such a block on the first machine this was run on, and the panel said
    /// "port already in use" about a port nothing was using.
    /// </remarks>
    [Fact]
    public async Task An_unavailable_port_is_replaced_rather_than_reported()
    {
        var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var blocked = ((IPEndPoint)taken.LocalEndpoint).Port;

        try
        {
            UsePort(blocked);
            var bridge = Build(new StubEndpoints());

            Assert.True(await bridge.StartAsync());
            Assert.True(bridge.IsRunning);
            Assert.Null(bridge.LastError);

            Assert.NotEqual(blocked, bridge.ActivePort);
            Assert.NotEqual(0, bridge.ActivePort);
            Assert.True(bridge.PortWasSubstituted);
        }
        finally
        {
            taken.Stop();
        }
    }

    /// <summary>Zero is a request, not a mistake: the operating system picks.</summary>
    [Fact]
    public async Task Port_zero_means_choose_one()
    {
        UsePort(0);
        var bridge = Build(new StubEndpoints());

        Assert.True(await bridge.StartAsync());

        Assert.NotEqual(0, bridge.ActivePort);
        Assert.False(bridge.PortWasSubstituted);   // nothing was substituted; this is what was asked for
    }

    /// <summary>
    /// The fallback must not turn into a restart loop: the running port no longer matches the setting on
    /// purpose, so the comparison that decides "is a restart due" cannot be made against it.
    /// </summary>
    [Fact]
    public async Task A_substituted_port_does_not_restart_the_bridge_for_ever()
    {
        var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        taken.Start();

        try
        {
            UsePort(((IPEndPoint)taken.LocalEndpoint).Port);
            var bridge = Build(new StubEndpoints());
            await bridge.StartAsync();
            var first = bridge.ActivePort;

            await bridge.StartAsync();
            await bridge.StartAsync();

            Assert.Equal(first, bridge.ActivePort);
        }
        finally
        {
            taken.Stop();
        }
    }

    /// <summary>
    /// A start that fails must not unpair every device.
    /// </summary>
    /// <remarks>
    /// The failure path went through the same code as "the user switched this off", which forgets the
    /// stored devices — so a machine that had just woken with no address yet, or could not write its
    /// certificate, permanently unpaired every phone. The same mistake as forgetting them on shutdown,
    /// reached precisely when things are already going badly.
    /// </remarks>
    [Fact]
    public async Task A_failed_start_keeps_the_paired_devices()
    {
        var store = new RememberingStore();
        UseAnyPort();

        // An address this machine does not have, so binding fails however the port is chosen.
        var bridge = Build(new StubEndpoints(), IPAddress.Parse("203.0.113.1"), store);
        bridge.Pairing.TryRedeem(bridge.Pairing.IssuePairingToken(), "iPhone", out var token);

        Assert.False(await bridge.StartAsync());
        Assert.NotNull(bridge.LastError);

        // On disk, and in this process. Keeping the file while clearing the list in memory was half a
        // fix: the phone stayed paired according to one and unpaired according to the other, so it could
        // not reconnect until mTiles was restarted.
        Assert.True(new PhonePairing(store: store).TryAuthorize(token));
        Assert.True(bridge.Pairing.TryAuthorize(token));
        Assert.Single(bridge.Sessions);
    }

    private sealed class RememberingStore : IPhoneSessionStore
    {
        private List<PhoneSession> _saved = [];

        public IReadOnlyList<PhoneSession> Load() => [.. _saved];

        public void Save(IReadOnlyList<PhoneSession> sessions) => _saved = [.. sessions];
    }

    /// <summary>
    /// A failed start must leave nothing behind.
    /// </summary>
    /// <remarks>
    /// The server subscribes to <c>PhonePairing.SessionEnded</c> as it starts, and the certificate holds
    /// an operating-system key handle — but neither is assigned to the manager's fields until the socket
    /// is listening, so a failure dropped both on the floor. The subscription is what makes it visible:
    /// it roots the dead server, so the count only ever goes up, and on a machine whose configured port
    /// cannot be bound this path is taken on every single start.
    /// </remarks>
    [Fact]
    public async Task A_failed_start_leaves_nothing_subscribed()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints(), IPAddress.Parse("203.0.113.1"), null);

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.False(await bridge.StartAsync());

        // Counted, not inferred. The first version of this test revoked a pairing and asserted nothing
        // threw — which was true before the fix as well, because a leaked handler on a dead server does
        // its work silently. A leak that is invisible by construction has to be looked at directly.
        Assert.Equal(0, SessionEndedSubscribers(bridge.Pairing));
    }

    /// <summary>
    /// How many handlers are on <c>PhonePairing.SessionEnded</c>.
    /// </summary>
    /// <remarks>
    /// Reflection, because the count is the only observable form of this bug and exposing it in the
    /// production type would be an API that exists for one assertion.
    /// </remarks>
    private static int SessionEndedSubscribers(PhonePairing pairing)
    {
        var field = typeof(PhonePairing).GetField(nameof(PhonePairing.SessionEnded),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        return ((Delegate?)field.GetValue(pairing))?.GetInvocationList().Length ?? 0;
    }

    /// <summary>The counter itself has to be able to see one, or it proves nothing.</summary>
    [Fact]
    public async Task The_subscription_counter_sees_a_running_server()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());

        Assert.True(await bridge.StartAsync());

        Assert.Equal(1, SessionEndedSubscribers(bridge.Pairing));
    }

    /// <summary>
    /// A port no socket can be asked for is treated as "choose one for me".
    /// </summary>
    /// <remarks>
    /// <c>settings.json</c> is a file the user can open. An out-of-range number reaches <c>Listen</c> as
    /// an <see cref="ArgumentOutOfRangeException"/>, which is not a bind failure — so it never took the
    /// fallback, and the feature was simply dead with an obscure message.
    /// One number, not a table of them: what a nonsensical port is <em>clamped to</em> is pinned purely by
    /// the test below, which costs nothing, and this one exists only to prove the clamp is reached from a
    /// real start — a second case of it spins up another Kestrel to learn the same thing.
    /// </remarks>
    [Fact]
    public async Task A_nonsensical_port_still_starts()
    {
        UsePort(999999);
        var bridge = Build(new StubEndpoints());

        Assert.True(await bridge.StartAsync());
        Assert.InRange(bridge.ActivePort, 1, 65535);
    }

    [Fact]
    public void An_impossible_port_is_clamped_to_automatic()
    {
        Assert.Equal(0, PhoneBridgeManager.ClampPort(999999));
        Assert.Equal(0, PhoneBridgeManager.ClampPort(-1));
        Assert.Equal(18091, PhoneBridgeManager.ClampPort(18091));
        Assert.Equal(0, PhoneBridgeManager.ClampPort(0));
    }

    // ── reconfiguring ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The other blocker. The server fixes its allowed Host values and its certificate's names when it
    /// starts, so a bridge left running across a change of network kept answering for addresses this
    /// machine no longer has — and the panel would draw a perfectly good QR code for the new one, which
    /// met a bare 400.
    /// </summary>
    [Fact]
    public async Task A_changed_address_set_restarts_the_bridge()
    {
        UseAnyPort();
        var endpoints = new StubEndpoints();
        var bridge = Build(endpoints);
        await bridge.StartAsync();

        // Asserted through what the server answers, not through what the manager believes. Checking
        // Board or IsRunning would have passed against the broken version too: it kept a perfectly
        // healthy server running, just one configured for yesterday's network.
        Assert.Equal(HttpStatusCode.BadRequest, await ReachAsync(bridge.ActivePort, "10.1.2.3"));

        endpoints.Hosts.Add("10.1.2.3");          // a VPN came up, or the laptop joined another network
        await bridge.RefreshAsync();
        Assert.True(await bridge.StartAsync());

        // The port is read again rather than remembered: the restart binds a new socket, and with the
        // operating system choosing, that is a new number.
        Assert.NotEqual(HttpStatusCode.BadRequest, await ReachAsync(bridge.ActivePort, "10.1.2.3"));
        Assert.Null(bridge.LastError);
    }

    /// <summary>Asks the bridge for the page as a device arriving at <paramref name="host"/> would.</summary>
    private static async Task<HttpStatusCode> ReachAsync(int port, string host)
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/");
        request.Headers.Host = host;
        return (await client.SendAsync(request)).StatusCode;
    }

    /// <summary>
    /// Reconfiguring is not a shutdown. Connecting a VPN only ever *adds* a way to reach this machine, so
    /// having it unpair the phone in the user's hand would be the feature undoing itself.
    /// </summary>
    [Fact]
    public async Task Reconfiguring_keeps_paired_devices()
    {
        UseAnyPort();
        var endpoints = new StubEndpoints();
        var bridge = Build(endpoints);
        await bridge.StartAsync();

        Assert.True(bridge.Pairing.TryRedeem(bridge.Pairing.IssuePairingToken(), "iPhone", out _));

        endpoints.Hosts.Add("10.1.2.3");
        await bridge.RefreshAsync();
        await bridge.StartAsync();

        // That the reconfiguration actually happened, asserted through what the server answers. Checking
        // only the session count passed against a version that never restarted at all.
        Assert.NotEqual(HttpStatusCode.BadRequest, await ReachAsync(bridge.ActivePort, "10.1.2.3"));
        Assert.Single(bridge.Sessions);
    }

    [Fact]
    public async Task Stopping_for_real_drops_paired_devices()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());
        await bridge.StartAsync();
        bridge.Pairing.TryRedeem(bridge.Pairing.IssuePairingToken(), "iPhone", out _);

        await bridge.StopAsync();

        Assert.Empty(bridge.Sessions);
    }

    // ── when it may stop ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_open_panel_keeps_the_bridge_up()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());
        var hold = bridge.HoldOpen();
        await bridge.StartAsync();

        await bridge.StopIfUnneededAsync();
        Assert.True(bridge.IsRunning);

        hold.Dispose();
        await bridge.StopIfUnneededAsync();
        Assert.False(bridge.IsRunning);
    }

    [Fact]
    public async Task A_paired_device_keeps_the_bridge_up()
    {
        UseAnyPort();
        var bridge = Build(new StubEndpoints());
        await bridge.StartAsync();
        bridge.Pairing.TryRedeem(bridge.Pairing.IssuePairingToken(), "iPhone", out _);

        await bridge.StopIfUnneededAsync();
        Assert.True(bridge.IsRunning);

        bridge.Pairing.Revoke(bridge.Sessions[0].Id);
        await bridge.StopIfUnneededAsync();
        Assert.False(bridge.IsRunning);
    }

    /// <summary>"Keep running" means exactly that: nothing incidental takes it down.</summary>
    [Fact]
    public async Task The_keep_running_setting_outranks_everything()
    {
        UseAnyPort();
        _settings.Service.Settings.Phone.Enabled = true;
        var bridge = Build(new StubEndpoints());
        await bridge.StartAsync();

        await bridge.StopIfUnneededAsync();

        Assert.True(bridge.IsRunning);
    }

    // ── what a phone's transcript is composed with ──────────────────────────────────────────────────

    /// <summary>
    /// The phone has its own auto-Enter, because the gesture is not the same one: at the keyboard you can
    /// see what landed before pressing Enter, and holding a phone you often are not looking at the screen.
    /// </summary>
    [Fact]
    public void A_phone_transcript_uses_the_phones_own_auto_enter()
    {
        var settings = new AppSettings();
        settings.Speech.AutoSubmitEnter = true;
        settings.Phone.AutoSubmitEnter = false;

        Assert.False(PhoneBridgeManager.PhoneDelivery(settings).AutoSubmitEnter);

        settings.Phone.AutoSubmitEnter = true;
        Assert.True(PhoneBridgeManager.PhoneDelivery(settings).AutoSubmitEnter);
    }

    /// <summary>Spacing is a property of the text, not of the microphone, so that one is shared.</summary>
    [Fact]
    public void A_phone_transcript_keeps_the_shared_spacing_setting()
    {
        var settings = new AppSettings();
        settings.Speech.AppendTrailingSpace = false;

        Assert.False(PhoneBridgeManager.PhoneDelivery(settings).AppendTrailingSpace);
    }

    /// <summary>Keeps paired devices nowhere, so a test never writes into the running user's profile.</summary>
    private sealed class NowhereStore : IPhoneSessionStore
    {
        public IReadOnlyList<PhoneSession> Load() => [];

        public void Save(IReadOnlyList<PhoneSession> sessions) { }
    }

    /// <summary>A capture that reports itself present and records nothing.</summary>
    private sealed class SilentCapture : IAudioCapture
    {
        public bool IsAvailable => true;
        public bool IsRecording { get; private set; }

        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["silent"];

        public void Start(string deviceName) => IsRecording = true;

        public IRecordingHandle? Detach()
        {
            IsRecording = false;
            return null;
        }

        public float[] Finish(IRecordingHandle? recording) => [];

        public void Dispose() { }
    }
}
