using System.Security.Cryptography;
using System.Text;

namespace mTiles.Services.Phone;

/// <summary>A phone that has paired and may now stream audio.</summary>
/// <param name="Id">
/// A SHA-256 digest of the token the device presents — never the token itself.
/// <para>Sessions outlive the process, so they are written to disk, and a bearer token at rest is a
/// standing grant of terminal access to anyone who reads the file or an old backup of it. Keeping only
/// the digest makes the file useless for authenticating: it can say <em>that</em> a device is paired and
/// what it called itself, and it cannot be replayed. It doubles as the handle the panel revokes by, which
/// is why nothing anywhere needs the raw value after the cookie has been handed out.</para>
/// </param>
/// <param name="Label">How the device described itself, for the panel. Untrusted text — treat as display only.</param>
/// <param name="Established">When the pairing was redeemed.</param>
/// <param name="LastSeen">
/// When the device was last heard from. Drives the idle timeout.
/// <para>It used to say "when the device last sent anything", which was not what the code did: it was
/// only written when a session was <em>authorised</em>, and that happens at the WebSocket handshake and
/// nowhere else. A phone that stayed connected all day — which is the whole point of "keep running" —
/// therefore idled out after eight hours <em>while it was being used</em>, and the sweep then revoked it
/// and closed the socket underneath it. See <see cref="PhonePairing.Touch"/>.</para>
/// </param>
internal sealed record PhoneSession(string Id, string Label, DateTimeOffset Established, DateTimeOffset LastSeen);

/// <summary>Where paired devices are remembered between runs.</summary>
/// <remarks>
/// An interface so the expiry rules can be tested without touching a disk, and so a test never writes
/// into the directory of whoever is running it.
/// </remarks>
internal interface IPhoneSessionStore
{
    IReadOnlyList<PhoneSession> Load();

    void Save(IReadOnlyList<PhoneSession> sessions);
}

/// <summary>Keeps them in <c>phone/sessions.json</c>, beside the bridge's certificate.</summary>
internal sealed class PhoneSessionStore(string? directory = null) : IPhoneSessionStore
{
    private readonly string _path =
        Path.Combine(directory ?? AppPaths.GetPhoneDirectory(), "sessions.json");

    public IReadOnlyList<PhoneSession> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];

            return System.Text.Json.JsonSerializer.Deserialize<List<PhoneSession>>(
                File.ReadAllText(_path), JsonDefaults.Options) ?? [];
        }
        catch (Exception ex)
        {
            // A file that cannot be read means nobody is paired, which is the safe direction: the user
            // rescans a QR code. It must never stop the bridge starting.
            System.Diagnostics.Trace.TraceWarning("Paired phones could not be read: {0}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Writes through a temporary file and a move, the way <c>GitIgnoreFile</c> already does.
    /// </summary>
    /// <remarks>
    /// A truncated write leaves a file that parses as nothing, and the reader's answer to that is "nobody
    /// is paired" — so an interrupted save unpairs every device. A move is what makes the file either the
    /// old list or the new one and never half of either.
    /// </remarks>
    public void Save(IReadOnlyList<PhoneSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary,
                System.Text.Json.JsonSerializer.Serialize(sessions, JsonDefaults.Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Paired phones could not be saved: {0}", ex.Message);
        }
    }
}

/// <summary>
/// Who is allowed to talk to the phone bridge.
/// </summary>
/// <remarks>
/// <b>The thing being protected is not the audio — it is the keyboard.</b> Whoever reaches this server can
/// put text into the terminal the user is looking at, so an unauthenticated bridge on a LAN or a tailnet
/// is a remote shell for everyone on that network. That is why the QR code carries a secret rather than
/// just an address.
/// <para>Two tokens, deliberately. The <i>pairing</i> token is the one in the QR code: short-lived and
/// single-use, because a QR code is displayed on a screen other people can see and photograph, and may
/// sit in a phone's camera history for months. Redeeming it yields a <i>session</i> token that never
/// appears in a URL, in a QR code, or on screen. So a photographed QR code is worthless the moment the
/// user's own phone has used it, and worthless anyway two minutes later.</para>
/// <para>Pure and clock-injected so both expiries are testable without waiting for them.</para>
/// </remarks>
internal sealed class PhonePairing
{
    public PhonePairing(
        TimeSpan? pairingLifetime = null,
        TimeSpan? sessionIdleTimeout = null,
        Func<DateTimeOffset>? clock = null,
        IPhoneSessionStore? store = null)
    {
        _pairingLifetime = pairingLifetime ?? DefaultPairingLifetime;
        _sessionIdleTimeout = sessionIdleTimeout ?? DefaultSessionIdleTimeout;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _store = store;

        // Every entry is checked, not merely the file's syntax. `sessions.json` sits in a directory the
        // user can open, and a hand-edited `[null]` or an entry with no id would otherwise throw while
        // this object is being built — during the construction of the main window, so the application
        // does not start and says nothing about why. A file that cannot be believed means nobody is
        // paired, which is the safe direction: rescan a code.
        try
        {
            foreach (var session in _store?.Load() ?? [])
            {
                if (session is null || string.IsNullOrWhiteSpace(session.Id))
                    continue;

                _sessions[session.Id] = session;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Paired phones could not be restored: {0}", ex.Message);
            _sessions.Clear();
        }

        lock (_gate)
            DropExpired();
    }

    private readonly IPhoneSessionStore? _store;

    /// <summary>Long enough to unlock a phone and open the camera, short enough that a photograph of the
    /// screen taken across the room is useless by the time it is looked at.</summary>
    public static readonly TimeSpan DefaultPairingLifetime = TimeSpan.FromMinutes(2);

    /// <summary>A paired phone that has said nothing for this long has been put in a pocket.</summary>
    public static readonly TimeSpan DefaultSessionIdleTimeout = TimeSpan.FromHours(8);

    private readonly TimeSpan _pairingLifetime;
    private readonly TimeSpan _sessionIdleTimeout;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PhoneSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// How many codes may be live at once.
    /// </summary>
    /// <remarks>
    /// More than one because the panel shows two — "same Wi-Fi" and "remote" — and a single live token
    /// would mean the code the user did not scan last silently fails. That is worse than it sounds: the
    /// point of showing both is that the ranking might be wrong, so the code most likely to be scanned
    /// after a failure is precisely the one a single-token scheme would have invalidated. Small, because
    /// each is a secret on a screen, and nothing legitimate needs a fifth.
    /// </remarks>
    internal const int MaxPendingCodes = 4;

    /// <summary>Raised whenever the set of paired devices changes, so the panel can redraw.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised with the id of a session that is no longer valid, so its socket can be closed.
    /// </summary>
    /// <remarks>
    /// Revoking only forgot the session, and the socket a device already held went on working: the
    /// membership test happens at the handshake and never again. So "Disconnect this device" disconnected
    /// nothing — the phone could keep dictating into the user's terminal for as long as it stayed
    /// connected, which is the one thing that button exists to stop.
    /// </remarks>
    public event Action<string>? SessionEnded;

    /// <summary>Whether any displayed code could still be redeemed.</summary>
    public bool HasPendingCode
    {
        get
        {
            lock (_gate)
            {
                DropExpiredCodes();
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>
    /// Mints the token for a freshly displayed QR code.
    /// </summary>
    public string IssuePairingToken()
    {
        var token = NewToken();
        lock (_gate)
        {
            DropExpiredCodes();

            // Oldest out first, so a panel left open and refreshing cannot accumulate live secrets.
            while (_pending.Count >= MaxPendingCodes)
                _pending.Remove(_pending.MinBy(entry => entry.Value).Key);

            _pending[token] = _now() + _pairingLifetime;
        }
        return token;
    }

    /// <summary>
    /// Whether a code minted earlier can still be redeemed.
    /// </summary>
    /// <remarks>
    /// The panel asks rather than works it out. Which code falls out when a new one is issued is this
    /// class's rule — oldest first, once <see cref="MaxPendingCodes"/> are live — and the panel used to
    /// hold a second copy of it, complete with its own issue counter, to decide which row to take off
    /// screen. Two implementations of one rule, and the panel's copy had already been wrong once (it
    /// trimmed in display order, which is reversed, so it kept the dead code and discarded the live one).
    /// Asking also covers what the copy could not see at all: a code that simply expired while displayed.
    /// </remarks>
    public bool IsPairingTokenLive(string token)
    {
        lock (_gate)
            return _pending.TryGetValue(token, out var expires) && expires > _now();
    }

    /// <summary>
    /// Withdraws every displayed code without issuing another. Called when the panel closes.
    /// </summary>
    /// <remarks>
    /// What makes closing the panel a meaningful act: a user who thinks the screen was photographed can
    /// invalidate what was on it, rather than waiting out the expiry.
    /// </remarks>
    public void ClearPairingTokens()
    {
        lock (_gate)
            _pending.Clear();
    }

    /// <summary>
    /// Exchanges a pairing token for a session token. False when the token is wrong, already used, or expired.
    /// </summary>
    public bool TryRedeem(string? offered, string label, out string sessionToken)
    {
        sessionToken = "";
        if (string.IsNullOrEmpty(offered))
            return false;

        var issued = NewToken();
        lock (_gate)
        {
            DropExpiredCodes();

            // Scanned rather than looked up, so the comparison stays constant-time: a dictionary probe on
            // the caller's string leaks, through timing, whether a prefix of a live token was guessed.
            var matched = _pending.Keys.FirstOrDefault(known => ConstantTimeEquals(known, offered));
            if (matched is null)
                return false;

            // Single use. That code is now spent, whoever else photographed the screen it was on. The
            // other displayed code stays live, because the user may still need it.
            _pending.Remove(matched);

            var now = _now();
            var id = Fingerprint(issued);
            _sessions[id] = new PhoneSession(id, Clean(label), now, now);
            Persist();
        }

        sessionToken = issued;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="offered"/> is a live session, marking it as seen if so.
    /// </summary>
    /// <remarks>
    /// Validating and touching together, because every caller does both and a caller that forgot the
    /// second would silently expire a phone that is streaming audio right now.
    /// </remarks>
    public bool TryAuthorize(string? offered) => TryAuthorize(offered, out _);

    /// <summary>
    /// As above, also handing back the session's id so a caller can be told when that session ends.
    /// </summary>
    public bool TryAuthorize(string? offered, out string sessionId)
    {
        sessionId = "";

        if (string.IsNullOrEmpty(offered))
            return false;

        // Hashed first, then looked up. A dictionary probe would leak, through timing, how much of a
        // guessed key matched — but the key here is a SHA-256 digest of what the caller sent, and to aim
        // at a digest prefix you would have to know the token already. Hashing is what removes the
        // channel; the lookup after it is ordinary.
        var id = Fingerprint(offered);

        List<string> expired;
        bool authorized;

        lock (_gate)
        {
            expired = DropExpired();

            if (!_sessions.TryGetValue(id, out var session))
            {
                authorized = false;
            }
            else
            {

                var now = _now();

                // Written back only when the clock has moved enough to matter. Persisting on every frame
                // of audio would rewrite the file dozens of times a second for a whole utterance.
                var worthSaving = now - session.LastSeen > TimeSpan.FromMinutes(5);
                _sessions[id] = session with { LastSeen = now };
                if (worthSaving)
                    Persist();

                authorized = true;
                sessionId = id;
            }
        }

        // Outside the lock. Raising an event while holding it invites a handler that reaches back in.
        Announce(expired);

        return authorized;
    }

    /// <summary>
    /// Marks a session as heard from, without re-checking it.
    /// </summary>
    /// <remarks>
    /// Called by the receive loop as frames arrive: membership was settled at the handshake, so this is
    /// not another authorisation, only the clock. Persisting is left to the same five-minute rule as
    /// authorisation, because a phone streaming audio produces dozens of these a second.
    /// </remarks>
    public void Touch(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
                return;

            var now = _now();
            var worthSaving = now - session.LastSeen > TimeSpan.FromMinutes(5);
            _sessions[id] = session with { LastSeen = now };

            if (worthSaving)
                Persist();
        }
    }

    /// <summary>Ends one device's pairing. Takes the <see cref="PhoneSession.Id"/>, not a token.</summary>
    public void Revoke(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(id);
            if (removed)
                Persist();
        }

        if (!removed)
            return;

        SessionEnded?.Invoke(id);
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends every pairing and withdraws the displayed codes. What "turn the bridge off" means.
    /// </summary>
    /// <param name="forget">
    /// Whether the devices are also forgotten on disk. False when the application is merely closing:
    /// shutting down is not a decision to unpair, and treating it as one would have made the stored
    /// sessions worthless — every run would erase what the previous one wrote, which is the exact
    /// opposite of the point.
    /// </param>
    public void RevokeAll(bool forget = true)
    {
        List<string> ended;
        lock (_gate)
        {
            ended = [.. _sessions.Keys];
            _sessions.Clear();
            _pending.Clear();

            if (forget)
                Persist();
        }

        if (ended.Count == 0)
            return;

        foreach (var id in ended)
            SessionEnded?.Invoke(id);

        Changed?.Invoke();
    }

    /// <summary>
    /// How many devices are paired, without sweeping and without raising anything.
    /// </summary>
    /// <remarks>
    /// <see cref="Sessions"/> drops what has expired and announces it, which is right for a panel about to
    /// draw a list and wrong for a caller holding a lock — and the caller that asks most often,
    /// <c>StopIfUnneededAsync</c>, is holding one. Counting is all it needs.
    /// </remarks>
    public int SessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>The devices paired right now, newest first.</summary>
    public IReadOnlyList<PhoneSession> Sessions
    {
        get
        {
            List<string> expired;
            IReadOnlyList<PhoneSession> sessions;

            lock (_gate)
            {
                expired = DropExpired();
                sessions = [.. _sessions.Values.OrderByDescending(s => s.Established)];
            }

            Announce(expired);

            return sessions;
        }
    }

    /// <summary>Drops whatever has gone stale, announcing it if anything did.</summary>
    /// <remarks>
    /// Nothing else notices an expiry on its own. Without something calling this, a device that timed
    /// out stayed on the panel and — worse — went on counting as "a phone is paired", which is one of the
    /// two things keeping the bridge listening on the network.
    /// </remarks>
    public void Sweep()
    {
        List<string> expired;
        lock (_gate)
            expired = DropExpired();

        Announce(expired);
    }

    /// <summary>Tells the world about sessions that have just gone. Never called with the lock held.</summary>
    private void Announce(List<string> ended)
    {
        if (ended.Count == 0)
            return;

        foreach (var id in ended)
            SessionEnded?.Invoke(id);

        Changed?.Invoke();
    }

    private void DropExpiredCodes()
    {
        var now = _now();
        foreach (var expired in _pending.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
            _pending.Remove(expired);
    }

    /// <summary>Removes stale sessions and returns their ids. Called with the lock held.</summary>
    private List<string> DropExpired()
    {
        var cutoff = _now() - _sessionIdleTimeout;
        var stale = _sessions.Where(s => s.Value.LastSeen < cutoff).Select(s => s.Key).ToList();

        foreach (var key in stale)
            _sessions.Remove(key);

        if (stale.Count > 0)
            Persist();

        return stale;
    }

    /// <summary>Writes the paired devices out. Called with the lock held.</summary>
    private void Persist() =>
        _store?.Save([.. _sessions.Values.OrderByDescending(session => session.Established)]);

    /// <summary>
    /// The SHA-256 digest of a token, base64, as stored and compared.
    /// </summary>
    /// <remarks>
    /// No salt and no work factor, deliberately. Those defend a low-entropy secret against an offline
    /// guessing attack; this one is 128 random bits, so there is nothing to guess and a slow hash would
    /// only make every request slower.
    /// </remarks>
    private static string Fingerprint(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>128 bits, URL-safe, and short enough that the QR code stays coarse enough to scan across a desk.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Compares without leaking, through timing, how much of the token was right.
    /// </summary>
    /// <remarks>
    /// The length is compared first and in the clear, which is safe: token length is a constant of this
    /// program, not a secret. What must not leak is <i>where</i> two equal-length tokens diverge, and
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> is what hides that.
    /// </remarks>
    private static bool ConstantTimeEquals(string expected, string offered)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(offered);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Trims a device label to something safe to put on screen.
    /// </summary>
    /// <remarks>
    /// The label arrives from the phone, so it is attacker-controlled in exactly the way a filename or a
    /// git branch name is. Control characters are dropped because a terminal application is the last
    /// place an escape sequence should reach by accident, and the length is capped because the panel has
    /// a fixed width.
    /// </remarks>
    private static string Clean(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "Phone";

        var cleaned = new string([.. label.Where(c => !char.IsControl(c))]).Trim();
        return cleaned.Length switch
        {
            0 => "Phone",
            > 40 => cleaned[..40],
            _ => cleaned,
        };
    }
}
