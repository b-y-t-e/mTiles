using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Who may talk to the phone bridge.
/// </summary>
/// <remarks>
/// What is being protected is the keyboard, not the audio: anything that reaches this server can type
/// into the terminal the user is looking at. The clock is injected so both expiries are provable without
/// waiting two minutes and eight hours for them.
/// </remarks>
public class PhonePairingTests
{
    private DateTimeOffset _now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private PhonePairing Create() => new(
        pairingLifetime: TimeSpan.FromMinutes(2),
        sessionIdleTimeout: TimeSpan.FromHours(8),
        clock: () => _now);

    [Fact]
    public void A_fresh_code_pairs_a_device()
    {
        var pairing = Create();
        var code = pairing.IssuePairingToken();

        Assert.True(pairing.TryRedeem(code, "iPhone", out var session));
        Assert.True(pairing.TryAuthorize(session));
        Assert.Equal("iPhone", Assert.Single(pairing.Sessions).Label);
    }

    /// <summary>
    /// A QR code is displayed on a screen other people can see, and lives in a phone's camera history
    /// afterwards. Single use is what makes a photograph of it worthless once the owner has scanned it.
    /// </summary>
    [Fact]
    public void A_code_cannot_be_redeemed_twice()
    {
        var pairing = Create();
        var code = pairing.IssuePairingToken();

        Assert.True(pairing.TryRedeem(code, "iPhone", out _));
        Assert.False(pairing.TryRedeem(code, "someone else", out _));
    }

    [Fact]
    public void A_code_expires()
    {
        var pairing = Create();
        var code = pairing.IssuePairingToken();

        _now += TimeSpan.FromMinutes(3);

        Assert.False(pairing.TryRedeem(code, "iPhone", out _));
        Assert.False(pairing.HasPendingCode);
    }

    /// <summary>
    /// The panel shows two codes at once — same Wi-Fi and remote — and the second exists precisely for
    /// when the first does not work. Issuing one must not quietly kill the other.
    /// </summary>
    [Fact]
    public void Both_displayed_codes_stay_live()
    {
        var pairing = Create();
        var lan = pairing.IssuePairingToken();
        var remote = pairing.IssuePairingToken();

        Assert.True(pairing.TryRedeem(remote, "iPhone", out _));
        Assert.True(pairing.TryRedeem(lan, "iPad", out _));
    }

    /// <summary>
    /// The panel asks the store which of its codes still work, rather than keeping its own copy of the rule.
    /// </summary>
    /// <remarks>
    /// Both halves matter to what is on screen: a code pushed out by a newer one, and a code that simply
    /// ran out of time while nobody was looking at it. Either way it scans perfectly and then reports an
    /// expired pairing, which is a worse answer than showing no code at all. The panel used to work this
    /// out itself, with its own issue counter, and had already got it wrong once.
    /// </remarks>
    [Fact]
    public void A_code_says_whether_it_can_still_be_redeemed()
    {
        var pairing = Create();

        var pushedOut = pairing.IssuePairingToken();
        Assert.True(pairing.IsPairingTokenLive(pushedOut));

        for (var i = 0; i < 4; i++)
            pairing.IssuePairingToken();

        Assert.False(pairing.IsPairingTokenLive(pushedOut));

        var timedOut = pairing.IssuePairingToken();
        Assert.True(pairing.IsPairingTokenLive(timedOut));

        _now += TimeSpan.FromMinutes(3);

        Assert.False(pairing.IsPairingTokenLive(timedOut));
        Assert.False(pairing.IsPairingTokenLive("never issued"));
    }

    /// <summary>Bounded, so a panel left open and refreshing cannot pile up live secrets.</summary>
    [Fact]
    public void Only_a_few_codes_may_be_live_at_once()
    {
        var pairing = Create();
        var first = pairing.IssuePairingToken();
        for (var i = 0; i < 4; i++)
            pairing.IssuePairingToken();

        Assert.False(pairing.TryRedeem(first, "iPhone", out _));
    }

    /// <summary>Closing the panel is a way to revoke what was on screen, not just to stop looking at it.</summary>
    [Fact]
    public void Clearing_withdraws_every_displayed_code()
    {
        var pairing = Create();
        var code = pairing.IssuePairingToken();

        pairing.ClearPairingTokens();

        Assert.False(pairing.TryRedeem(code, "iPhone", out _));
    }

    [Fact]
    public void A_wrong_or_empty_token_is_refused()
    {
        var pairing = Create();
        pairing.IssuePairingToken();

        Assert.False(pairing.TryRedeem("", "x", out _));
        Assert.False(pairing.TryRedeem(null, "x", out _));
        Assert.False(pairing.TryRedeem("not-a-real-token", "x", out _));
        Assert.False(pairing.TryAuthorize("nonsense"));
    }

    /// <summary>A phone left in a pocket loses its pairing; one that is streaming does not.</summary>
    [Fact]
    public void A_session_expires_when_idle_but_not_while_in_use()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var session);

        _now += TimeSpan.FromHours(6);
        Assert.True(pairing.TryAuthorize(session));   // also marks it seen

        _now += TimeSpan.FromHours(6);
        Assert.True(pairing.TryAuthorize(session));

        _now += TimeSpan.FromHours(9);
        Assert.False(pairing.TryAuthorize(session));
        Assert.Empty(pairing.Sessions);
    }

    /// <summary>
    /// An expiry has to be announced, not merely applied.
    /// </summary>
    /// <remarks>
    /// Nothing else notices one. A device that timed out stayed on the panel, and — worse — went on
    /// counting as "a phone is paired", which is one of the two things keeping the bridge listening on
    /// the network. With the setting off and the panel closed, one phone paired in the morning held the
    /// socket open for the rest of the day.
    /// </remarks>
    [Fact]
    public void Sweeping_drops_an_expired_device_and_says_so()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var token);

        var announced = 0;
        pairing.Changed += () => announced++;

        pairing.Sweep();
        Assert.Equal(0, announced);         // nothing has expired yet
        Assert.Single(pairing.Sessions);

        _now += TimeSpan.FromHours(9);
        pairing.Sweep();

        Assert.Equal(1, announced);
        Assert.Empty(pairing.Sessions);
        Assert.False(pairing.TryAuthorize(token));
    }

    /// <summary>Sweeping a set with nothing stale in it is silent, so the panel does not redraw for nothing.</summary>
    [Fact]
    public void Sweeping_changes_nothing_when_everything_is_live()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out _);

        var announced = 0;
        pairing.Changed += () => announced++;

        pairing.Sweep();
        pairing.Sweep();

        Assert.Equal(0, announced);
        Assert.Single(pairing.Sessions);
    }

    [Fact]
    public void Revoking_ends_one_device_and_revoke_all_ends_everything()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var one);
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPad", out var two);

        // By id, not by token: the panel only ever holds the id, and the token exists nowhere after the
        // cookie was handed out.
        pairing.Revoke(pairing.Sessions.Single(s => s.Label == "iPhone").Id);

        Assert.False(pairing.TryAuthorize(one));
        Assert.True(pairing.TryAuthorize(two));

        pairing.RevokeAll();
        Assert.False(pairing.TryAuthorize(two));
    }

    /// <summary>
    /// What the "keep running" setting promises: a phone paired yesterday reconnects without being shown
    /// a new code. Sessions lived only in memory, so that promise was broken by every restart.
    /// </summary>
    [Fact]
    public void A_pairing_survives_a_restart()
    {
        var store = new MemoryStore();

        var first = new PhonePairing(clock: () => _now, store: store);
        first.TryRedeem(first.IssuePairingToken(), "iPhone", out var token);

        var second = new PhonePairing(clock: () => _now, store: store);

        Assert.True(second.TryAuthorize(token));
        Assert.Equal("iPhone", Assert.Single(second.Sessions).Label);
    }

    /// <summary>
    /// The file is a record of who is paired, never a credential. Anyone reading it — or an old backup of
    /// it — must not be able to replay their way into someone's terminal.
    /// </summary>
    [Fact]
    public void What_is_stored_cannot_be_used_to_authenticate()
    {
        var store = new MemoryStore();
        var pairing = new PhonePairing(clock: () => _now, store: store);
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var token);

        var stored = Assert.Single(store.Saved);

        Assert.NotEqual(token, stored.Id);
        Assert.False(new PhonePairing(clock: () => _now, store: store).TryAuthorize(stored.Id));
    }

    /// <summary>
    /// Closing the application is not a decision to unpair. Forgetting on shutdown would have every run
    /// erase what the previous one wrote, which is the same as not storing anything.
    /// </summary>
    [Fact]
    public void Shutting_down_does_not_forget_paired_devices()
    {
        var store = new MemoryStore();
        var pairing = new PhonePairing(clock: () => _now, store: store);
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var token);

        pairing.RevokeAll(forget: false);

        Assert.True(new PhonePairing(clock: () => _now, store: store).TryAuthorize(token));
    }

    [Fact]
    public void Turning_the_bridge_off_does_forget_them()
    {
        var store = new MemoryStore();
        var pairing = new PhonePairing(clock: () => _now, store: store);
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var token);

        pairing.RevokeAll();

        Assert.False(new PhonePairing(clock: () => _now, store: store).TryAuthorize(token));
    }

    [Fact]
    public void A_session_that_expired_while_the_application_was_closed_does_not_come_back()
    {
        var store = new MemoryStore();
        var pairing = new PhonePairing(sessionIdleTimeout: TimeSpan.FromHours(8), clock: () => _now, store: store);
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPhone", out var token);
        pairing.RevokeAll(forget: false);

        _now += TimeSpan.FromDays(2);

        var reopened = new PhonePairing(sessionIdleTimeout: TimeSpan.FromHours(8), clock: () => _now, store: store);

        Assert.False(reopened.TryAuthorize(token));
        Assert.Empty(reopened.Sessions);
    }

    /// <summary>
    /// A stored file the user has edited into nonsense must not stop the application starting.
    /// </summary>
    /// <remarks>
    /// This is constructed while the main window is being built, so an exception here is not a failed
    /// feature but a program that does not open and says nothing about why — from a file sitting in a
    /// directory anyone can browse to.
    /// </remarks>
    [Fact]
    public void A_damaged_session_file_is_ignored_rather_than_fatal()
    {
        var store = new BrokenStore();

        var pairing = new PhonePairing(clock: () => _now, store: store);

        Assert.Empty(pairing.Sessions);
        Assert.False(pairing.TryAuthorize("anything"));
    }

    /// <summary>Hands back the shapes a hand-edited file can take: a null entry, and one with no id.</summary>
    private sealed class BrokenStore : IPhoneSessionStore
    {
        public IReadOnlyList<PhoneSession> Load() =>
        [
            null!,
            new PhoneSession("", "no id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new PhoneSession("   ", "blank id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        ];

        public void Save(IReadOnlyList<PhoneSession> sessions) { }
    }

    private sealed class MemoryStore : IPhoneSessionStore
    {
        public List<PhoneSession> Saved { get; private set; } = [];

        public IReadOnlyList<PhoneSession> Load() => [.. Saved];

        public void Save(IReadOnlyList<PhoneSession> sessions) => Saved = [.. sessions];
    }

    /// <summary>
    /// The label comes from the phone, so it is attacker-controlled. A terminal application is the last
    /// place an escape sequence should arrive by accident.
    /// </summary>
    [Fact]
    public void A_device_label_is_cleaned_before_it_reaches_the_screen()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "iPh[31mone\n", out _);

        var label = Assert.Single(pairing.Sessions).Label;
        Assert.DoesNotContain('', label);
        Assert.DoesNotContain('\n', label);
    }

    [Fact]
    public void A_blank_label_becomes_something_showable()
    {
        var pairing = Create();
        pairing.TryRedeem(pairing.IssuePairingToken(), "   ", out _);

        Assert.Equal("Phone", Assert.Single(pairing.Sessions).Label);
    }
}
