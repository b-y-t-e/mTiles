using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>Whether a phone's request to start streaming was taken up, and why not when it was not.</summary>
internal sealed record PhoneStreamOutcome(bool Accepted, string Message)
{
    public static PhoneStreamOutcome Ok { get; } = new(true, "");
}

/// <summary>
/// Where what arrives from a phone goes — audio, and the few keys its page can press — and what the
/// phone is told about it.
/// </summary>
/// <remarks>
/// The server's whole view of the rest of the application. It exists so that the transport — TLS,
/// WebSocket framing, pairing — can be tested and reasoned about without a dictation service, a tile or a
/// UI thread anywhere near it, and so that the server holds no opinion about what any of it is for.
/// </remarks>
internal interface IPhoneSink
{
    /// <summary>A phone has pressed and held. Returns whether recording actually started.</summary>
    Task<PhoneStreamOutcome> BeginAsync(int sampleRate);

    /// <summary>One frame of 16-bit little-endian mono PCM.</summary>
    void Write(ReadOnlySpan<byte> pcm);

    /// <summary>The phone let go. Transcription follows.</summary>
    Task EndAsync();

    /// <summary>The phone gave up, or its connection died mid-utterance. Nothing is transcribed.</summary>
    void CancelStream();

    /// <summary>
    /// A phone pressed one of the keys its page offers.
    /// </summary>
    /// <remarks>
    /// Nothing to do with dictation, and gated on nothing dictation is gated on: a machine with no speech
    /// model — or no microphone at all — can still be driven from the phone, which is the case this whole
    /// feature exists for.
    /// </remarks>
    /// <returns>Null when the key was delivered, otherwise why it was not. A refusal is worth a sentence
    /// because the phone is usually the only screen the user is looking at.</returns>
    Task<string?> PressKeyAsync(TileKey key);

    /// <summary>
    /// A phone pressed one of the actions the active tile offers.
    /// </summary>
    /// <remarks>
    /// The set is open — a tile kind registered later brings whatever actions it likes — but what a
    /// paired device may cause is still decided in this process rather than by the message: the manager
    /// looks the id up in the current actions of the currently active tile, and an unknown id gets the
    /// same answer malformed JSON gets.
    /// <para>This is the one member here allowed to take minutes — it runs the tile's own command, and
    /// Continue on a Goal tile is an implement/review loop. The server therefore starts it off the
    /// receive loop rather than awaiting it there, which is what keeps a phone able to speak and press
    /// Enter while the action it asked for is still running.</para>
    /// </remarks>
    /// <returns>Null when the action was carried out, otherwise why it was not.</returns>
    Task<string?> InvokeActionAsync(string id);

    /// <summary>What to tell a phone that has just connected: current state, and which tile it is aimed at.</summary>
    string DescribeState();

    /// <summary>And what that tile can be asked to do.</summary>
    string DescribeActions();
}

/// <summary>
/// The HTTPS server a phone's browser talks to.
/// </summary>
/// <remarks>
/// <b>Why Kestrel, when the database bridge uses <see cref="HttpListener"/>.</b> Two hard requirements
/// that <c>HttpListener</c> cannot meet in-process. It can only serve HTTPS against a certificate bound to
/// the port by <c>netsh http sslcert</c>, which needs administrator rights — unacceptable for a feature
/// that has to work on a first run — and TLS is not optional here, because a browser hands out no
/// microphone at all outside a secure context. The alternative was a hand-written HTTP and WebSocket
/// implementation over <c>SslStream</c>, on the one listener in this application that accepts connections
/// from the network rather than from loopback. That is the last place to be writing a parser.
/// <para><b>Everything is one file.</b> The page, its styles and its script are a single embedded
/// document with a strict content-security policy. There is nothing to fetch, so there is nothing to
/// serve, and the routing below is three paths long.</para>
/// </remarks>
internal sealed class PhoneBridgeServer(
    PhonePairing pairing,
    IPhoneSink sink,
    Action<string> onReachedVia) : IAsyncDisposable
{
    /// <summary>The session cookie's name. Never appears in a URL, a QR code, or on screen.</summary>
    private const string SessionCookie = "mtiles_phone";

    /// <summary>
    /// The largest message accepted, 64 KB. A frame of audio is a few kilobytes; the headroom is for the
    /// control messages and for a client that batches. Bounded because the sender is a network peer.
    /// </summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>
    /// How many sockets may be open at once.
    /// </summary>
    /// <remarks>
    /// Pairing already limits who may open one, but nothing limited how many a paired device could hold —
    /// and each one costs a receive loop and a send chain. Generous enough that a phone reconnecting
    /// before its old socket has finished closing is never refused.
    /// </remarks>
    private const int MaxConnections = 8;

    private readonly List<Connection> _connections = [];
    private readonly Lock _connectionGate = new();

    private WebApplication? _app;
    private HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _app is not null;

    /// <summary>
    /// How many devices have a socket open right now.
    /// </summary>
    /// <remarks>
    /// Not the same as how many are paired, and the difference is visible to the user: a session survives
    /// a change of network but the cookie that carries it does not — it was set for the address the phone
    /// paired on — so after the machine moves, a phone is still <em>paired</em> and no longer
    /// <em>connected</em>. The panel used to claim the latter on the strength of the former.
    /// </remarks>
    public int ConnectedCount
    {
        get { lock (_connectionGate) return _connections.Count; }
    }

    /// <summary>
    /// Raised when a device connects or disconnects.
    /// </summary>
    /// <remarks>
    /// The panel distinguishes "paired" from "connected", and without this it could only learn the second
    /// by being redrawn for some other reason: a phone that reconnected left the header saying "paired,
    /// but not connected right now" until something else happened to refresh it.
    /// </remarks>
    public event Action? ConnectionsChanged;

    /// <summary>
    /// The port actually bound, which is not always the one asked for.
    /// </summary>
    /// <remarks>
    /// Read back from the server rather than assumed, because <c>0</c> means "any free port" and the
    /// answer is only known after the socket exists. Everything user-facing — the URL in the QR code,
    /// what the panel says — has to use this and not the setting.
    /// </remarks>
    public int BoundPort { get; private set; }

    /// <summary>
    /// Starts listening on every interface.
    /// </summary>
    /// <param name="tls">
    /// The certificates to choose between. Chosen per connection by the name the client asked for, because
    /// this one socket answers for every address on the machine and no single certificate covers them all
    /// on a machine that has Tailscale.
    /// </param>
    /// <param name="allowedHosts">
    /// The host names and addresses a request's <c>Host</c> header may carry. Defence in depth against
    /// DNS rebinding: a page on some other site can make a browser resolve its own domain to this
    /// machine's address, and the pairing token is what stops that being useful — but a request whose
    /// Host is a name we never advertised has no legitimate explanation, so it is refused before anything
    /// else looks at it.
    /// </param>
    /// <param name="bindTo">
    /// Which address to listen on. Every interface in the application — a phone on the network is the
    /// point — and loopback in the tests, so running <c>dotnet test</c> does not raise a firewall prompt
    /// on the developer's machine every time.
    /// </param>
    public async Task StartAsync(int port, PhoneTlsMaterial tls, IReadOnlyList<string> allowedHosts,
        IPAddress? bindTo = null)
    {
        if (_app is not null)
            return;

        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });

        // A desktop application has no console for these to go to, and the ones worth keeping are already
        // written through Trace by the code here.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(bindTo ?? IPAddress.Any, port, listen =>
                listen.UseHttps(https => https.ServerCertificateSelector = (_, name) => tls.Select(name)));
            options.Limits.MaxRequestBodySize = MaxMessageBytes;
            options.AddServerHeader = false;
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            // The half-built host is released here, by the only code holding a reference to it. A caller
            // seeing the exception reasonably assumes nothing was created — and on a machine where the
            // configured port cannot be bound, this path is taken on *every* start.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _app = app;
        BoundPort = ResolveBoundPort(app, port);

        // Subscribed only once there is something to disconnect, and by this object rather than the
        // manager, because this is the only one that knows which socket belongs to which pairing.
        // Subscribing before the bind left a handler on PhonePairing for ever whenever the bind failed,
        // holding a dead server alive and answering SessionEnded on its behalf.
        pairing.SessionEnded += Disconnect;

        Trace.TraceInformation("Phone bridge listening on port {0} for {1}",
            BoundPort, string.Join(", ", allowedHosts));
    }

    /// <summary>Asks the running server which port it ended up on.</summary>
    private static int ResolveBoundPort(WebApplication app, int requested)
    {
        if (requested != 0)
            return requested;

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        foreach (var address in addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        // Unconditionally, and before the early return: `_app` is null exactly when the start failed,
        // which is the case that most needs the handler taken off. Unsubscribing something never
        // subscribed is a no-op, so there is nothing to guard.
        pairing.SessionEnded -= Disconnect;

        var app = _app;
        _app = null;
        BoundPort = 0;
        if (app is null)
            return;

        await CloseConnectionsAsync().ConfigureAwait(false);

        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Trace.TraceWarning("Phone bridge did not stop cleanly: {0}", ex.Message); }

        try { await app.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    /// <summary>
    /// Pushes a message to the phone whose recording is in progress, if any.
    /// </summary>
    /// <remarks>
    /// For anything belonging to one utterance — the transcript above all. What somebody dictated is not
    /// news for every paired device: a second phone, or a browser left open on the near machine, would
    /// have been shown it.
    /// </remarks>
    public void SendToStreamOwner(string json)
    {
        Connection? owner;
        lock (_connectionGate)
            owner = _connections.FirstOrDefault(connection => connection.OwnsStream);

        owner?.Post(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Closes whatever <paramref name="sessionId"/> still has open.</summary>
    /// <remarks>
    /// Called when a pairing is revoked or expires. Fire-and-forget: a socket that will not close politely
    /// is torn down by its own receive loop, and the user's click must not wait on the network.
    /// </remarks>
    public void Disconnect(string sessionId)
    {
        List<Connection> going;
        lock (_connectionGate)
            going = [.. _connections.Where(c => c.SessionId == sessionId)];

        foreach (var connection in going)
        {
            // Revoked here and now. Closing a WebSocket is a handshake: the frame goes out and the
            // connection stays usable until the peer answers — so asking politely and waiting for the
            // receive loop to notice left a revoked phone dictating into the terminal for as long as it
            // chose to ignore the request. That is the one thing this button exists to stop.
            connection.Revoked = true;

            // The recording, if this connection had one, ends immediately and not when the socket does.
            // Ownership is cleared *after* cancelling, so the receive loop's finally does not cancel a
            // second time — the earlier mistake was clearing it *instead of* cancelling.
            if (connection.OwnsStream)
            {
                connection.OwnsStream = false;
                sink.CancelStream();
            }

            _ = CloseAsync(connection, WebSocketCloseStatus.PolicyViolation, "pairing ended");
        }
    }

    /// <summary>
    /// Closes this end of a socket.
    /// </summary>
    /// <remarks>
    /// <c>CloseOutputAsync</c>, not <c>CloseAsync</c>. The latter sends the close frame and then *waits*
    /// for the peer's — which means receiving, and this connection already has a receive in flight in its
    /// own pump loop. A socket permits one at a time, so every disconnection threw
    /// <see cref="InvalidOperationException"/> and was caught and logged; it appeared to work only because
    /// the frame had already gone out by then. Sending and leaving the pump to observe the answer is what
    /// the two halves are for.
    /// </remarks>
    private static async Task CloseAsync(Connection connection, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (connection.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await connection.Socket.CloseOutputAsync(status, reason, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceInformation("Closing a phone socket failed: {0}", ex.Message);
        }
    }

    /// <summary>Pushes a message to every connected phone. Used for state changes, which are global.</summary>
    public void Broadcast(string json)
    {
        List<Connection> connections;
        lock (_connectionGate)
            connections = [.. _connections];

        var payload = Encoding.UTF8.GetBytes(json);
        foreach (var connection in connections)
            connection.Post(payload);
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!IsHostAllowed(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        if (path.StartsWith("/p/", StringComparison.Ordinal))
        {
            await PairAsync(context, path[3..]).ConfigureAwait(false);
            return;
        }

        if (path == "/ws")
        {
            await SocketAsync(context).ConfigureAwait(false);
            return;
        }

        await RootAsync(context, path).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the page to an already-paired device, and an explanation to anything else.
    /// </summary>
    /// <remarks>
    /// <b>Without this, a paired phone was one page refresh from being locked out.</b> The only route to
    /// the page was <c>/p/{token}</c>, and pairing tokens are single-use by design — so a phone that
    /// reloaded, or was locked and reopened its browser, held a perfectly valid session and could reach
    /// nothing with it. It had to be handed a fresh QR code from a machine that might be in another
    /// building, which makes the whole "keep running so a paired phone reconnects on its own" setting a
    /// promise the server could not keep.
    /// </remarks>
    private async Task RootAsync(HttpContext context, string path)
    {
        ApplySecurityHeaders(context);

        if (path == "/" && pairing.TryAuthorize(context.Request.Cookies[SessionCookie]))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Page.Value).ConfigureAwait(false);
            return;
        }

        // Anything else: say what this is, and nothing about whether a token would have worked. A person
        // who typed the address in gets an explanation; a scanner gets nothing.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(Notice(
            "mTiles", "Scan the QR code shown in mTiles to dictate from this device.")).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges the token from the QR code for a session cookie and serves the page.
    /// </summary>
    /// <remarks>
    /// The redeem is also the moment we learn something no amount of ranking could work out: which of
    /// this machine's addresses the phone actually reached it on. That is reported through
    /// <c>onReachedVia</c> and pinned, so the next QR code is a measurement rather than a guess.
    /// </remarks>
    private async Task PairAsync(HttpContext context, string token)
    {
        ApplySecurityHeaders(context);

        var label = DeviceLabel(context.Request.Headers.UserAgent.ToString());
        if (!pairing.TryRedeem(token, label, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Notice(
                "This code has expired", "Show the QR code in mTiles again and rescan it."))
                .ConfigureAwait(false);
            return;
        }

        context.Response.Cookies.Append(SessionCookie, session, new CookieOptions
        {
            HttpOnly = true,          // the page's own script never needs it; the socket sends it anyway
            Secure = true,

            // Lax, not Strict. This cookie is set on a response that is *also* a redirect, at the end of
            // a navigation that began outside the browser entirely — a camera app opening a scanned URL —
            // and Strict is defined against exactly that shape: several browsers withhold the cookie on
            // the redirect that follows. The cost of being wrong is not a retry, it is the end of the
            // road, because the pairing token has already been spent by the time the redirect is issued;
            // the phone would land on a page telling it to scan a code that no longer works. Lax still
            // withholds the cookie from cross-site subresource requests and cross-site POSTs, which is
            // what it is here for: everything this page does afterwards is same-site.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            // Deliberately far longer than the session it names. The server decides when a pairing is
            // over — it slides the session on every request — and the cookie is only the carrier. Giving
            // the cookie the session's own eight hours meant a phone in daily use lost its cookie after
            // eight hours from *pairing*, however recently it had been used, and had to rescan a code for
            // a session that was still perfectly alive.
            MaxAge = TimeSpan.FromDays(30),
        });

        try { onReachedVia(context.Request.Host.Host); }
        catch (Exception ex) { Trace.TraceWarning("Recording the reached-via host failed: {0}", ex.Message); }

        // Redirected rather than answered, so the URL carrying the pairing token never becomes the page
        // the phone is sitting on. Serving it directly left the secret in the address bar, in the
        // browser's history and in whatever the user's phone syncs that to — for a token that is already
        // spent, but which was readable over the shoulder for as long as the page stayed open. An HTTP
        // redirect leaves no history entry of its own, so the trail really does end here.
        context.Response.Redirect("/", permanent: false);
    }

    private async Task SocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!IsOriginAllowed(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!pairing.TryAuthorize(context.Request.Cookies[SessionCookie], out var sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        // The session is remembered on the connection, because membership is only tested at the
        // handshake: without it there is no way back from a session to the socket it opened, and
        // revoking a device left that device connected and dictating.
        var connection = new Connection(socket, sessionId);

        // Counted and taken under one lock. Checking the count and then adding left two simultaneous
        // handshakes both seeing room and both being let in — a small overshoot, but the cap exists
        // precisely because nothing else bounds how many sockets a paired device may hold.
        bool admitted;
        lock (_connectionGate)
        {
            admitted = _connections.Count < MaxConnections;
            if (admitted)
                _connections.Add(connection);
        }

        if (!admitted)
        {
            await CloseAsync(connection, WebSocketCloseStatus.PolicyViolation, "too many connections")
                .ConfigureAwait(false);
            connection.Dispose();
            return;
        }

        ConnectionsChanged?.Invoke();

        try
        {
            await PumpAsync(connection, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Trace.TraceInformation("Phone socket closed: {0}", ex.Message);
        }
        finally
        {
            lock (_connectionGate)
                _connections.Remove(connection);

            ConnectionsChanged?.Invoke();

            // Only the connection that started the recording may end it. A second phone dropping off the
            // network used to cancel whatever the *first* one was in the middle of saying — the panel
            // supports several paired devices, so that is reachable by ordinary use, not a corner case.
            if (connection.OwnsStream)
                sink.CancelStream();

            connection.Dispose();
        }
    }

    private async Task PumpAsync(Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        var socket = connection.Socket;

        connection.Post(Encoding.UTF8.GetBytes(sink.DescribeState()));
        connection.Post(Encoding.UTF8.GetBytes(sink.DescribeActions()));

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (message.Length + result.Count > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // A revoked connection is finished, whatever it goes on sending. The close frame may not be
            // answered for a long time, or ever.
            if (connection.Revoked)
                return;

            // Heard from. Membership was settled at the handshake, so this is the clock and nothing more
            // — without it a phone streaming all day still idled out of its own session after eight hours.
            pairing.Touch(connection.SessionId);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Audio, on the hot path: straight into the capture with no allocation and no parsing.
                // Only from the connection that started the recording — otherwise a second phone could
                // interleave its own samples into somebody else's sentence.
                if (connection.OwnsStream)
                    sink.Write(message.GetBuffer().AsSpan(0, (int)message.Length));
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            await HandleControlAsync(connection, text).ConfigureAwait(false);
        }
    }

    private async Task HandleControlAsync(Connection connection, string json)
    {
        string? type;
        string? keyName;
        string? actionId;
        int sampleRate;

        try
        {
            using var document = JsonDocument.Parse(json);
            type = document.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            keyName = document.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
            actionId = document.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
            sampleRate = document.RootElement.TryGetProperty("sampleRate", out var r) && r.TryGetInt32(out var v)
                ? v : 0;
        }
        // A peer sending nonsense is disconnected by nothing; it simply gets no reply — and that has to
        // hold for *well-formed* nonsense too. `{"key":123}` parses, so JsonException never fires;
        // GetString throws InvalidOperationException instead, which is how JsonElement says "that is
        // there, but it is not the kind you asked for". Uncaught it left this method, passed both
        // catches in PumpAsync and reached Kestrel, which drops the socket — and the pump's finally
        // cancels whatever recording the connection owned on the way out. A number where a string
        // belongs, from a paired device, killing a live dictation.
        //
        // Caught by type rather than by guarding each read: TryGetInt32 throws the same thing on a
        // string, and the next property added here would have needed remembering. Nothing else runs
        // inside this block, so there is no other meaning it could be swallowing.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return;
        }

        switch (type)
        {
            case "begin":
                // A second begin from a connection that is already recording is ignored outright.
                // Assigning the outcome to OwnsStream instead was a way to lose a recording for good: the
                // manager refuses the second request because it is already recording, so the flag went
                // *false* — and from that moment the live recording had no owner, which meant no `end`,
                // no `cancel` and not even a disconnection could stop it. It ran to the five-minute cap
                // with the tile stuck in "recording" and the phone unable to do anything about it.
                if (connection.OwnsStream)
                    return;

                // Rates outside this are not microphones. Rejected rather than trusted, because the value
                // sizes a resampling kernel and comes from the network.
                if (sampleRate is < 8_000 or > 192_000)
                {
                    Refuse(connection, "Unsupported sample rate.");
                    return;
                }

                var outcome = await sink.BeginAsync(sampleRate).ConfigureAwait(false);
                if (outcome.Accepted)
                    connection.OwnsStream = true;
                else
                    Refuse(connection, outcome.Message);
                return;

            case "end":
                if (!connection.OwnsStream)
                    return;
                connection.OwnsStream = false;
                await sink.EndAsync().ConfigureAwait(false);
                return;

            case "cancel":
                if (!connection.OwnsStream)
                    return;
                connection.OwnsStream = false;
                sink.CancelStream();
                return;

            // Deliberately not tied to a recording: the whole point is to answer a prompt the agent is
            // waiting on, which is a thing you do between utterances rather than during one. Any paired
            // device may press — the keyboard is what pairing protects, and it protects it at the
            // handshake.
            case "key":
                // A key name this build does not know is nonsense from a peer, and nonsense gets the same
                // answer as malformed JSON: none. Naming what was rejected would be the one message here
                // that describes the sender to itself.
                if (!PhoneKeys.TryParse(keyName, out var pressed))
                    return;

                // Its own type, not "error". The page shows both in the same box, but "error" is the
                // answer to *this device's dictation attempt* and the page undoes its own optimistic
                // state on one — it assumes transcription has started the moment the talk button is
                // let go, since waiting for the round trip is waiting through the exact moment the
                // user reaches for Enter. A key refusal sharing that word cleared the assumption on
                // behalf of a recording that was somebody else's, re-enabling the keys for the length
                // of their utterance: the very race the gate exists for. It also had the page release
                // a microphone over a keystroke.
                if (await sink.PressKeyAsync(pressed).ConfigureAwait(false) is { } refusal)
                    connection.Post(Message("keyError", refusal));
                return;

            // Not tied to a recording either, and for the same reason: driving an agent from the sofa is
            // half dictating a line and half answering what it stops on.
            case "action":
                if (actionId is not { Length: > 0 })
                    return;

                // Started, not awaited, and that is load-bearing. A tile action is the one thing a phone
                // can ask for that is not short: Continue on a Goal tile runs the whole implement/review
                // loop, minutes of it, and a Git push that fails ends in a message box somebody has to
                // walk over to the computer and click. Awaited here it would hold *this connection's*
                // receive loop for all of that — no ReceiveAsync, so the audio frames of the sentence
                // being spoken meanwhile go nowhere, `end` and `key` are never parsed, and the phone,
                // still being sent state down the independent write chain, looks perfectly alive. That
                // is exactly the sofa the actions were built for.
                //
                // Nothing here serialises two presses, because the tile already does: the id is checked
                // against what it offers *now*, on the UI thread, at the moment of the press
                // (PhoneTileActions.IsAllowed), so an action that is already running is refused for
                // being disabled rather than queued behind itself.
                _ = RunActionAsync(connection, actionId);
                return;
        }
    }

    /// <summary>
    /// Runs a tile action away from the receive loop and answers the phone once it is finished.
    /// </summary>
    /// <remarks>
    /// The refusal is posted rather than returned because by the time it exists the message it answers
    /// was parsed long ago. <see cref="Connection.Post"/> is the same ordered chain everything else to
    /// this phone goes down, and it is a no-op once the connection has been disposed, so an action that
    /// outlives its socket answers into nothing instead of throwing.
    /// <para>Its own type, not "error", for exactly the reason keyError is its own — the page treats
    /// "error" as the answer to *its* dictation attempt and unwinds its optimistic microphone state on
    /// one, so a refused action must not cancel somebody's recording.</para>
    /// <para>Nothing awaits the returned task, so an escaping exception would be unobserved on a
    /// thread-pool thread. The sink already wraps the tile's own command; this covers the sink itself
    /// failing, and tells the phone the same thing the sink would have.</para>
    /// </remarks>
    private async Task RunActionAsync(Connection connection, string actionId)
    {
        try
        {
            if (await sink.InvokeActionAsync(actionId).ConfigureAwait(false) is { } why)
                connection.Post(Message("actionError", why));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Running a tile action from a phone failed: {0}", ex);
            connection.Post(Message("actionError", "mTiles could not do that."));
        }
    }

    /// <summary>
    /// Says no to a <c>begin</c>, and says what mTiles is doing instead.
    /// </summary>
    /// <remarks>
    /// <para>The state is the load-bearing half. A page sets its own "recording" the moment it sends
    /// <c>begin</c> — it cannot wait for an answer, because the microphone is already live and the
    /// first syllable is being captured — so a release that happens before the refusal arrives leaves
    /// it having assumed an utterance is on its way to be transcribed. Nothing was, and nothing else
    /// would ever have said so: state is broadcast from dictation's own transitions, and a refused
    /// begin causes none. The page was left with its keys disabled and no message, until the socket
    /// dropped.</para>
    /// <para>It is also what tells the two kinds of refusal apart, which the page cannot do from the
    /// words: <em>"mTiles is already recording"</em> comes with <c>recording</c> and <em>"dictation is
    /// switched off"</em> with <c>idle</c>, so a page that clears its assumption on the second does not
    /// clear it on the first — and the first is somebody else's utterance, which the phone's keys must
    /// stay out of the way of. Answering that from the one place that knows beats every rule the page
    /// could have guessed at.</para>
    /// </remarks>
    private void Refuse(Connection connection, string why)
    {
        connection.Post(Message("error", why));
        connection.Post(Encoding.UTF8.GetBytes(sink.DescribeState()));
    }

    private static byte[] Message(string type, string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, message });

    private async Task CloseConnectionsAsync()
    {
        List<Connection> connections;
        lock (_connectionGate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            // CloseOutputAsync for the same reason as above: the pump is still receiving.
            await CloseAsync(connection, WebSocketCloseStatus.EndpointUnavailable, "shutting down")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a request's <c>Host</c> is one we advertised.
    /// </summary>
    /// <remarks>
    /// An empty set refuses everything. It used to allow everything — unreachable in practice, since the
    /// set is filled before the socket opens, but a security check whose degenerate case is "let it
    /// through" is the wrong way round however unreachable that case looks today.
    /// </remarks>
    private bool IsHostAllowed(string host) => _allowedHosts.Contains(host);

    /// <summary>
    /// Whether a WebSocket handshake came from a page this server served.
    /// </summary>
    /// <remarks>
    /// The cookie is <c>SameSite=Lax</c>, so a browser should already withhold it from a cross-site
    /// handshake — but "should" is doing a lot of work there, and a session cookie is the only thing
    /// between a page on some other origin and a socket that types into a terminal. Checking the origin
    /// is the conventional second lock on a WebSocket and costs one comparison.
    /// <para>An absent <c>Origin</c> is allowed: browsers always send one on a WebSocket handshake, and
    /// non-browser clients — the tests, a script somebody writes — never do. Refusing those would be
    /// refusing the case that is not a browser at all, which is not what this defends against.</para>
    /// </remarks>
    private bool IsOriginAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && IsHostAllowed(uri.Host);
    }

    private static void ApplySecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;

        // 'unsafe-inline' is unavoidable and safe here: the entire page is one document served by this
        // process, so there is no external origin to restrict and no user content in it. blob: is for the
        // AudioWorklet, which can only be loaded from a URL. connect-src is the part that matters — it
        // pins the socket to this server, so an injected script could not exfiltrate audio anywhere.
        //
        // The socket's origin is named outright as well as covered by 'self'. Whether 'self' extends to a
        // wss: URL on the same host is a corner of CSP that browsers have disagreed about — WebKit in
        // particular blocked it for several releases — and iOS Safari is the first platform this feature
        // is used from. Getting it wrong fails in the worst possible way: the page loads, the microphone
        // opens, the user speaks, and nothing arrives, with the reason visible only in a console nobody
        // has open on a phone. This costs one source expression and removes the question.
        //
        // The literal host, not wss: as a bare scheme. A bare scheme would allow the page to open a
        // socket to any server anywhere, which is exactly the exfiltration this directive exists to
        // prevent — and Host has already been checked against the allow-list above, so it is ours to
        // repeat rather than user input to trust.
        var socketOrigin = $"wss://{context.Request.Host.Value}";

        headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'unsafe-inline' blob:; style-src 'unsafe-inline'; "
            + $"connect-src 'self' {socketOrigin}; worker-src blob:; img-src data:; base-uri 'none'; "
            + "form-action 'none'; frame-ancestors 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
    }

    private static string Notice(string heading, string body) =>
        "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'>"
        + "<body style='font:16px system-ui;padding:2rem;background:#16162a;color:#c9c9d4'>"
        + $"<h1 style='font-size:1.2rem'>{WebUtility.HtmlEncode(heading)}</h1>"
        + $"<p>{WebUtility.HtmlEncode(body)}</p>";

    /// <summary>A phone's own name for itself, taken from the one header that carries anything like one.</summary>
    private static string DeviceLabel(string userAgent) =>
        userAgent switch
        {
            var ua when ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) => "iPhone",
            var ua when ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iPad",
            var ua when ua.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android phone",
            var ua when ua.Length > 0 => "Browser",
            _ => "Phone",
        };

    /// <summary>The page, read once from the assembly.</summary>
    private static readonly Lazy<string> Page = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("phone.html", StringComparison.Ordinal));

        if (name is null)
            return "<!doctype html><p>The dictation page is missing from this build.";

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// One connected phone, with its outgoing messages serialised.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="WebSocket"/> permits exactly one send at a time</b> — a second overlapping
    /// <c>SendAsync</c> throws rather than queueing. Posting without awaiting was right (a stalled phone
    /// must not hold up the dictation service's state change) but posting without serialising was not:
    /// a state change and the transcript that follows it are milliseconds apart, so on any connection
    /// slower than a LAN the transcript was the message that got thrown away — the one thing the user was
    /// waiting for, lost precisely when the network was bad enough to make them look at the phone.
    /// <para>A continuation chain rather than a semaphore, because order matters as much as exclusion:
    /// "transcribing" arriving after the transcript reads as a phone that never finished.</para>
    /// </remarks>
    private sealed class Connection(WebSocket socket, string sessionId) : IDisposable
    {
        private readonly Lock _chainGate = new();
        private Task _chain = Task.CompletedTask;
        private bool _closed;

        public WebSocket Socket => socket;

        /// <summary>Which pairing this connection belongs to, so ending that pairing can end this.</summary>
        public string SessionId => sessionId;

        /// <summary>
        /// Set when the pairing behind this connection has ended. Nothing it sends afterwards is acted on.
        /// </summary>
        /// <remarks>
        /// Separate from the socket's state because closing is a handshake and this is not: authority ends
        /// the moment it is withdrawn, whatever the peer does about the close frame.
        /// </remarks>
        public bool Revoked
        {
            get => Volatile.Read(ref _revoked);
            set => Volatile.Write(ref _revoked, value);
        }

        private bool _revoked;

        /// <summary>
        /// Whether the recording in progress is this connection's. Only its owner may end it.
        /// </summary>
        /// <remarks>
        /// Written by this connection's receive loop and read from two other threads — the delivery
        /// callback looking for whom to send a transcript to, and a revocation closing sockets. As a
        /// plain auto-property nothing obliged either of those to see the write, and the way that shows
        /// up is the worst of the three: the transcript finds no owner and the person holding the phone
        /// is told nothing at all.
        /// </remarks>
        public bool OwnsStream
        {
            get => Volatile.Read(ref _ownsStream);
            set => Volatile.Write(ref _ownsStream, value);
        }

        private bool _ownsStream;

        public void Post(byte[] payload)
        {
            lock (_chainGate)
            {
                if (_closed)
                    return;

                _chain = _chain.ContinueWith(
                    _ => SendAsync(payload), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        private async Task SendAsync(byte[] payload)
        {
            if (socket.State != WebSocketState.Open)
                return;

            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The receive loop is what notices a dead socket and tears the connection down; a failed
                // send only has to not become an unobserved exception on the thread pool.
                Trace.TraceInformation("Sending to a phone failed: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            lock (_chainGate)
                _closed = true;
        }
    }
}
