using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The transport: who gets the page, who gets a socket, and what reaches the audio sink.
/// </summary>
/// <remarks>
/// A real Kestrel on loopback with a real self-signed certificate, driven by a real
/// <see cref="ClientWebSocket"/> — because the things worth testing here are exactly the ones a fake
/// transport would define away: whether a cookie survives, whether two overlapping sends collide,
/// whether one phone's disconnection cancels another's recording. <see cref="IPhoneAudioSink"/> is the
/// seam that lets all of that run with no dictation service, no tile and no UI thread.
/// </remarks>
public sealed class PhoneBridgeServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly string _certificateDirectory =
        Path.Combine(Path.GetTempPath(), "mtiles-phone-tests-" + Guid.NewGuid().ToString("N"));

    private PhonePairing _pairing = null!;
    private FakeSink _sink = null!;
    private PhoneBridgeServer _server = null!;
    private PhoneTlsMaterial _tls = null!;
    private int _port;
    private readonly List<string> _reachedVia = [];

    public async Task InitializeAsync()
    {
        _pairing = new PhonePairing();
        _sink = new FakeSink();
        _port = FreePort();

        var certificate = new SelfSignedCertificateSource(_certificateDirectory)
            .TryGet(["localhost", "127.0.0.1"]);
        Assert.NotNull(certificate);
        _tls = new PhoneTlsMaterial([certificate]);

        _server = new PhoneBridgeServer(_pairing, _sink, host =>
        {
            lock (_reachedVia) _reachedVia.Add(host);
        });

        await _server.StartAsync(_port, _tls, ["localhost", "127.0.0.1"], IPAddress.Loopback);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _tls.Dispose();
        try { Directory.Delete(_certificateDirectory, true); } catch { }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string Root => $"https://localhost:{_port}";

    /// <summary>A client that accepts the self-signed certificate, exactly as a phone does once asked.</summary>
    private static HttpClient Client(CookieContainer cookies) =>
        new(new HttpClientHandler
        {
            CookieContainer = cookies,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });

    // ── pairing over HTTP ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_code_serves_the_page_and_sets_a_session_cookie()
    {
        var cookies = new CookieContainer();
        using var client = Client(cookies);

        var response = await client.GetAsync($"{Root}/p/{_pairing.IssuePairingToken()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mTiles dictation", await response.Content.ReadAsStringAsync());
        Assert.Contains(cookies.GetCookies(new Uri(Root)).Cast<Cookie>(), c => c.Name == "mtiles_phone");
    }

    /// <summary>The pin is only as good as this: it is the one address we know actually worked.</summary>
    [Fact]
    public async Task Pairing_reports_the_address_the_device_arrived_on()
    {
        using var client = Client(new CookieContainer());

        await client.GetAsync($"{Root}/p/{_pairing.IssuePairingToken()}");

        lock (_reachedVia)
            Assert.Equal("localhost", Assert.Single(_reachedVia));
    }

    [Fact]
    public async Task A_wrong_or_spent_code_is_refused()
    {
        using var client = Client(new CookieContainer());
        var code = _pairing.IssuePairingToken();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"{Root}/p/not-a-real-token")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/p/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/p/{code}")).StatusCode);
    }

    /// <summary>
    /// The regression behind B2: pairing codes are single-use, so without a route that accepts the
    /// session cookie a paired phone was one page refresh away from being locked out until somebody
    /// showed it a new QR code — on a machine that may be in another building.
    /// </summary>
    [Fact]
    public async Task A_paired_device_can_reload_the_page()
    {
        var cookies = new CookieContainer();
        using var client = Client(cookies);
        await client.GetAsync($"{Root}/p/{_pairing.IssuePairingToken()}");

        var reloaded = await client.GetAsync($"{Root}/");

        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        Assert.Contains("mTiles dictation", await reloaded.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unpaired_device_gets_no_page()
    {
        using var client = Client(new CookieContainer());

        var response = await client.GetAsync($"{Root}/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("mTiles dictation", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A request whose Host we never advertised has no legitimate explanation.</summary>
    [Fact]
    public async Task A_foreign_host_header_is_refused()
    {
        using var client = Client(new CookieContainer());
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/p/{_pairing.IssuePairingToken()}");
        request.Headers.Host = "attacker.example";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    // ── the socket ──────────────────────────────────────────────────────────────────────────────────

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var token = _pairing.IssuePairingToken();
        Assert.True(_pairing.TryRedeem(token, "test", out var session));

        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.Cookies = new CookieContainer();
        socket.Options.Cookies.Add(new Uri(Root), new Cookie("mtiles_phone", session) { Path = "/" });

        using var timeout = new CancellationTokenSource(Patience);
        await socket.ConnectAsync(new Uri($"wss://localhost:{_port}/ws"), timeout.Token);
        return socket;
    }

    [Fact]
    public async Task A_socket_without_a_session_is_refused()
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var timeout = new CancellationTokenSource(Patience);
        var failure = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"wss://localhost:{_port}/ws"), timeout.Token));

        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task Begin_audio_and_end_reach_the_sink_in_order()
    {
        using var socket = await ConnectAsync();

        await SendTextAsync(socket, """{"type":"begin","sampleRate":48000}""");
        await _sink.Began.Task.WaitAsync(Patience);
        Assert.Equal(48_000, _sink.SampleRate);

        await SendBinaryAsync(socket, [1, 2, 3, 4]);
        await SendTextAsync(socket, """{"type":"end"}""");
        await _sink.Ended.Task.WaitAsync(Patience);

        Assert.Equal([1, 2, 3, 4], _sink.Audio);
    }

    /// <summary>The rate sizes a resampling kernel and arrives from the network.</summary>
    [Fact]
    public async Task An_absurd_sample_rate_is_refused_before_the_sink_sees_it()
    {
        using var socket = await ConnectAsync();

        await SendTextAsync(socket, """{"type":"begin","sampleRate":2000000}""");

        var message = await ReadUntilAsync(socket, "error");
        Assert.Contains("sample rate", message);
        Assert.False(_sink.Began.Task.IsCompleted);
    }

    /// <summary>
    /// A second <c>begin</c> from a connection that is already recording must not orphan the recording.
    /// </summary>
    /// <remarks>
    /// Assigning the refusal to the ownership flag lost the recording for good: the manager says no
    /// because it is already recording, the flag went false, and from that moment nothing could stop what
    /// was running — not <c>end</c>, not <c>cancel</c>, not even disconnecting. It ran to the five-minute
    /// cap with the tile stuck in "recording".
    /// </remarks>
    [Fact]
    public async Task A_repeated_begin_does_not_orphan_the_recording()
    {
        using var socket = await ConnectAsync();

        await SendTextAsync(socket, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        // The second one is refused by the manager in production; here the sink accepts everything, so
        // what is being pinned is the server's own rule: while this connection owns a stream, begin is
        // ignored outright and ownership is never cleared by it.
        await SendTextAsync(socket, """{"type":"begin","sampleRate":16000}""");

        await SendBinaryAsync(socket, [7, 7]);
        await SendTextAsync(socket, """{"type":"end"}""");

        await _sink.Ended.Task.WaitAsync(Patience);
        Assert.Equal([7, 7], _sink.Audio);
    }

    [Fact]
    public async Task Audio_from_a_connection_that_never_began_is_ignored()
    {
        using var socket = await ConnectAsync();

        await SendBinaryAsync(socket, [9, 9, 9, 9]);
        // Round-trip a control message so the server has demonstrably processed what came before it.
        await SendTextAsync(socket, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        Assert.Empty(_sink.Audio);
    }

    /// <summary>
    /// W4: the panel supports several paired devices, so a second phone dropping off the network is
    /// ordinary use — and it used to cancel whatever the first one was in the middle of saying.
    /// </summary>
    [Fact]
    public async Task One_device_disconnecting_does_not_cancel_another_devices_recording()
    {
        using var recorder = await ConnectAsync();
        var bystander = await ConnectAsync();

        await SendTextAsync(recorder, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        bystander.Dispose();                       // an abrupt drop, not a polite close
        await Task.Delay(300);

        Assert.Equal(0, _sink.Cancellations);
    }

    [Fact]
    public async Task A_recorders_own_disconnection_cancels_its_recording()
    {
        var recorder = await ConnectAsync();

        await SendTextAsync(recorder, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        recorder.Dispose();

        var deadline = DateTime.UtcNow + Patience;
        while (_sink.Cancellations == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, _sink.Cancellations);
    }

    /// <summary>
    /// B1. A <see cref="WebSocket"/> permits one send at a time and throws on a second, so broadcasting
    /// without serialising dropped whichever message lost the race — in practice the transcript, which
    /// follows a state change by milliseconds. Fifty in a row makes that certain rather than occasional.
    /// </summary>
    [Fact]
    public async Task Rapid_broadcasts_all_arrive_and_keep_their_order()
    {
        using var socket = await ConnectAsync();
        await ReadUntilAsync(socket, "state");   // the greeting the server sends on connect

        const int count = 50;
        for (var i = 0; i < count; i++)
            _server.Broadcast(JsonSerializer.Serialize(new { type = "text", message = i.ToString() }));

        var received = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var json = await ReadUntilAsync(socket, "text");
            received.Add(JsonDocument.Parse(json).RootElement.GetProperty("message").GetString()!);
        }

        Assert.Equal(Enumerable.Range(0, count).Select(i => i.ToString()), received);
    }

    /// <summary>
    /// A transcript belongs to the phone that spoke it.
    /// </summary>
    /// <remarks>
    /// Broadcasting it put one person's dictation on every paired device: a second phone in the room, or
    /// the browser left open on the near machine, was shown what was said into the other. The state
    /// messages still go to everyone, because "mTiles is busy" is true for all of them.
    /// </remarks>
    [Fact]
    public async Task A_transcript_reaches_only_the_device_that_spoke()
    {
        using var speaker = await ConnectAsync();
        using var bystander = await ConnectAsync();

        await SendTextAsync(speaker, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        _server.SendToStreamOwner(JsonSerializer.Serialize(new { type = "text", message = "hello" }));

        // A marker afterwards, to everyone. Sends are ordered per connection, so if the bystander's next
        // "text" is the marker then the transcript never reached it — asserted without waiting on a
        // message that is supposed to never arrive.
        _server.Broadcast(JsonSerializer.Serialize(new { type = "text", message = "marker" }));

        Assert.Equal("hello", await ReadMessageAsync(speaker));
        Assert.Equal("marker", await ReadMessageAsync(bystander));
    }

    private static async Task<string> ReadMessageAsync(ClientWebSocket socket) =>
        JsonDocument.Parse(await ReadUntilAsync(socket, "text"))
            .RootElement.GetProperty("message").GetString()!;

    /// <summary>
    /// Revoking a pairing has to end the connection it opened.
    /// </summary>
    /// <remarks>
    /// Membership is tested at the handshake and never again, so forgetting the session left the device
    /// connected and still able to dictate into the user's terminal — which is exactly what the panel's
    /// "Disconnect this device" button claims to stop. The same path ends a session that has expired.
    /// </remarks>
    [Fact]
    public async Task Revoking_a_pairing_closes_the_socket_it_opened()
    {
        using var revoked = await ConnectAsync();
        using var bystander = await ConnectAsync();

        var target = _pairing.Sessions.Last();   // the older of the two: the one `revoked` holds
        _pairing.Revoke(target.Id);

        using var timeout = new CancellationTokenSource(Patience);
        var buffer = new byte[4096];

        // Reads until the close frame arrives, which is what a disconnection looks like from here.
        while (revoked.State == WebSocketState.Open)
        {
            var result = await revoked.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }

        Assert.NotEqual(WebSocketState.Open, revoked.State);
        Assert.Equal(WebSocketState.Open, bystander.State);
    }

    /// <summary>
    /// A handshake claiming an origin this server never served is refused.
    /// </summary>
    /// <remarks>
    /// The cookie is SameSite=Lax, so a browser should withhold it from a cross-site handshake anyway —
    /// but a session cookie is the only thing between a page on another origin and a socket that types
    /// into a terminal, and one string comparison is a cheap second lock.
    /// </remarks>
    [Fact]
    public async Task A_socket_from_a_foreign_origin_is_refused()
    {
        var token = _pairing.IssuePairingToken();
        Assert.True(_pairing.TryRedeem(token, "test", out var session));

        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.Cookies = new CookieContainer();
        socket.Options.Cookies.Add(new Uri(Root), new Cookie("mtiles_phone", session) { Path = "/" });
        socket.Options.SetRequestHeader("Origin", "https://attacker.example");

        using var timeout = new CancellationTokenSource(Patience);
        var failure = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"wss://localhost:{_port}/ws"), timeout.Token));

        Assert.Contains("403", failure.Message);
    }

    /// <summary>The page's own origin is, of course, allowed.</summary>
    [Fact]
    public async Task A_socket_from_our_own_origin_is_allowed()
    {
        var token = _pairing.IssuePairingToken();
        Assert.True(_pairing.TryRedeem(token, "test", out var session));

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.Cookies = new CookieContainer();
        socket.Options.Cookies.Add(new Uri(Root), new Cookie("mtiles_phone", session) { Path = "/" });
        socket.Options.SetRequestHeader("Origin", Root);

        using var timeout = new CancellationTokenSource(Patience);
        await socket.ConnectAsync(new Uri($"wss://localhost:{_port}/ws"), timeout.Token);

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    /// <summary>
    /// A frame larger than the bridge accepts closes the socket rather than being buffered.
    /// </summary>
    /// <remarks>
    /// The bound exists because the sender is a network peer: without it, a client that never sets
    /// <c>EndOfMessage</c> grows a MemoryStream on this machine until it runs out.
    /// </remarks>
    [Fact]
    public async Task An_oversized_frame_closes_the_socket()
    {
        using var socket = await ConnectAsync();

        using var timeout = new CancellationTokenSource(Patience);
        var oversized = new byte[128 * 1024];

        try
        {
            await socket.SendAsync(oversized, WebSocketMessageType.Binary, true, timeout.Token);

            // Reads until the server's close frame arrives; the send itself may well succeed first.
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (WebSocketException)
        {
            // An abrupt close is the same answer.
        }

        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.Empty(_sink.Audio);
    }

    /// <summary>
    /// A paired device cannot open unlimited sockets.
    /// </summary>
    /// <remarks>
    /// Pairing bounds *who* may connect and nothing bounded *how many* — and each socket costs a receive
    /// loop and a send chain on the machine the user is working at.
    /// </remarks>
    [Fact]
    public async Task Too_many_sockets_are_refused()
    {
        var open = new List<ClientWebSocket>();

        try
        {
            for (var i = 0; i < 8; i++)
                open.Add(await ConnectAsync());

            Assert.All(open, socket => Assert.Equal(WebSocketState.Open, socket.State));

            // The ninth is refused rather than accepted and quietly dropped: the client is told, either by
            // a failed handshake or by a close frame arriving at once.
            var extra = await Task.Run(async () =>
            {
                try
                {
                    var socket = await ConnectAsync();
                    using var timeout = new CancellationTokenSource(Patience);
                    var buffer = new byte[1024];

                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, timeout.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }

                    return socket.State;
                }
                catch (WebSocketException)
                {
                    return WebSocketState.Closed;
                }
            });

            Assert.NotEqual(WebSocketState.Open, extra);
        }
        finally
        {
            foreach (var socket in open)
                socket.Dispose();
        }
    }

    /// <summary>
    /// Revoking ends the recording at once, not when the phone gets round to answering.
    /// </summary>
    /// <remarks>
    /// Closing a WebSocket is a handshake: the frame goes out and the connection stays usable until the
    /// peer replies. Asking politely and leaving the receive loop to notice therefore let a revoked phone
    /// go on dictating into the terminal for as long as it chose to ignore the request — which is exactly
    /// what the panel's "Disconnect this device" button exists to stop.
    /// </remarks>
    [Fact]
    public async Task Revoking_ends_the_recording_without_waiting_for_the_phone()
    {
        using var socket = await ConnectAsync();

        await SendTextAsync(socket, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        // The client never answers the close frame — it simply stops reading, as a wedged page would.
        _pairing.Revoke(_pairing.Sessions.Single().Id);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_sink.Cancellations == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, _sink.Cancellations);
    }

    /// <summary>And nothing it sends afterwards is acted on.</summary>
    [Fact]
    public async Task A_revoked_device_cannot_go_on_sending_audio()
    {
        using var socket = await ConnectAsync();

        await SendTextAsync(socket, """{"type":"begin","sampleRate":16000}""");
        await _sink.Began.Task.WaitAsync(Patience);

        _pairing.Revoke(_pairing.Sessions.Single().Id);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_sink.Cancellations == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        try
        {
            await SendBinaryAsync(socket, [5, 5, 5, 5]);
            await Task.Delay(300);
        }
        catch (WebSocketException)
        {
            // The socket may already be gone; either way nothing reached the sink.
        }

        Assert.Empty(_sink.Audio);
    }

    /// <summary>
    /// The panel is told when the number of live sockets changes.
    /// </summary>
    /// <remarks>
    /// It distinguishes "paired" from "connected", and without this event it could only learn the second
    /// by being redrawn for some other reason — so a phone that reconnected left the header saying
    /// "paired, but not connected right now" until something unrelated happened to refresh it.
    /// </remarks>
    [Fact]
    public async Task Connecting_and_disconnecting_are_both_announced()
    {
        var changes = 0;
        _server.ConnectionsChanged += () => Interlocked.Increment(ref changes);

        var socket = await ConnectAsync();
        Assert.Equal(1, Volatile.Read(ref changes));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        socket.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref changes) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.Equal(0, _server.ConnectedCount);
    }

    /// <summary>
    /// The page may talk to this server and nowhere else — and the socket is named, not merely implied.
    /// </summary>
    /// <remarks>
    /// Two separate properties in one header, and neither is obvious from reading it. The first is that
    /// <c>default-src 'none'</c> plus a closed <c>connect-src</c> is what stops an injected script
    /// shipping the microphone somewhere; deleting a directive here would weaken that silently, because
    /// nothing about the page stops working. The second is that the socket's own <c>wss:</c> origin is
    /// spelled out rather than left to <c>'self'</c>, which browsers have not agreed about — WebKit
    /// blocked that combination for several releases, and iOS Safari is the first platform this is used
    /// from. That failure is invisible: the page loads, the microphone opens, and no audio arrives.
    /// </remarks>
    [Fact]
    public async Task The_page_may_open_a_socket_to_this_server_and_reach_nowhere_else()
    {
        using var client = Client(new CookieContainer());
        var response = await client.GetAsync($"{Root}/p/{_pairing.IssuePairingToken()}");

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'none'", csp);
        Assert.Contains($"connect-src 'self' wss://{response.RequestMessage!.RequestUri!.Authority};", csp);
        Assert.Contains("frame-ancestors 'none'", csp);

        // A bare scheme would let the page open a socket to any server anywhere, which is the whole of
        // what connect-src is here to prevent.
        Assert.DoesNotContain("wss:;", csp);
        Assert.DoesNotContain("wss: ", csp);
    }

    /// <summary>
    /// The session cookie: not readable by script, never sent in the clear, and Lax rather than Strict.
    /// </summary>
    /// <remarks>
    /// The last of those is the one worth a test, because it looks like the weaker choice and is not.
    /// This cookie is set on a response that is also a redirect, ending a navigation that began outside
    /// the browser altogether — a camera app opening a scanned URL — and several browsers withhold a
    /// Strict cookie across exactly that. The cost of being wrong is not a retry: the pairing token has
    /// already been spent by the time the redirect goes out, so the phone lands on a page telling it to
    /// rescan a code that no longer works. Anyone tightening this to Strict should have to delete a test
    /// that says why.
    /// </remarks>
    [Fact]
    public async Task The_session_cookie_is_script_proof_https_only_and_survives_the_scan()
    {
        // Not following the redirect: the cookie is set on the 302 itself, so a client that chases it
        // hands back the headers of the page instead.
        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });

        var response = await client.GetAsync($"{Root}/p/{_pairing.IssuePairingToken()}");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connecting_device_is_told_the_current_state()
    {
        using var socket = await ConnectAsync();

        Assert.Contains("\"state\"", await ReadUntilAsync(socket, "state"));
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────

    private static async Task SendTextAsync(ClientWebSocket socket, string json)
    {
        using var timeout = new CancellationTokenSource(Patience);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
    }

    private static async Task SendBinaryAsync(ClientWebSocket socket, byte[] payload)
    {
        using var timeout = new CancellationTokenSource(Patience);
        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, timeout.Token);
    }

    private static async Task<string> ReadUntilAsync(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(Patience);
        var buffer = new byte[8192];

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (JsonDocument.Parse(json).RootElement.TryGetProperty("type", out var actual) &&
                actual.GetString() == type)
                return json;
        }
    }

    private sealed class FakeSink : IPhoneAudioSink
    {
        private readonly List<byte> _audio = [];

        public TaskCompletionSource Began { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SampleRate { get; private set; }
        public int Cancellations;

        public byte[] Audio
        {
            get { lock (_audio) return [.. _audio]; }
        }

        public Task<PhoneStreamOutcome> BeginAsync(int sampleRate)
        {
            SampleRate = sampleRate;
            Began.TrySetResult();
            return Task.FromResult(PhoneStreamOutcome.Ok);
        }

        public void Write(ReadOnlySpan<byte> pcm)
        {
            var copy = pcm.ToArray();
            lock (_audio) _audio.AddRange(copy);
        }

        public Task EndAsync()
        {
            Ended.TrySetResult();
            return Task.CompletedTask;
        }

        public void CancelStream() => Interlocked.Increment(ref Cancellations);

        public string DescribeState() =>
            JsonSerializer.Serialize(new { type = "state", state = "idle", tile = "Terminal #1" });
    }
}
