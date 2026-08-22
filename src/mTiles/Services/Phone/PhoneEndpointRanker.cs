namespace mTiles.Services.Phone;

/// <summary>An endpoint with the score the ranking gave it and the reason, in words, for that score.</summary>
/// <param name="Reason">
/// Shown under the address in the panel. Not decoration: when the top pick is wrong the user has to
/// choose between several private addresses that look identical, and "Wi-Fi, has a default route" is the
/// only thing on screen that tells them which one their phone might be on.
/// </param>
internal sealed record RankedPhoneEndpoint(PhoneEndpoint Endpoint, int Score, string Reason)
{
    public PhoneEndpointAudience Audience => Endpoint.Audience;
}

/// <summary>
/// What the panel shows: one recommended endpoint per audience, plus everything else for when both
/// guesses are wrong.
/// </summary>
/// <param name="SameNetwork">Best endpoint for a phone on this machine's own network. Null when there is none.</param>
/// <param name="Remote">Best endpoint for a phone anywhere else. Null when no tunnel is configured.</param>
/// <param name="Preferred">
/// Which of the two the session location says to put first. Never hides the other — it only decides the
/// order, because the cost of being wrong about the order is a glance and the cost of being wrong about
/// what to show is a user who cannot dictate at all.
/// </param>
/// <param name="All">Every candidate, best first, for the "different network?" list.</param>
internal sealed record PhoneEndpointBoard(
    RankedPhoneEndpoint? SameNetwork,
    RankedPhoneEndpoint? Remote,
    PhoneEndpointAudience Preferred,
    IReadOnlyList<RankedPhoneEndpoint> All)
{
    public static PhoneEndpointBoard Empty { get; } =
        new(null, null, PhoneEndpointAudience.SameNetwork, []);

    /// <summary>The two recommendations in the order they should appear, skipping the absent one.</summary>
    public IReadOnlyList<RankedPhoneEndpoint> Recommended =>
        Preferred == PhoneEndpointAudience.Remote
            ? new[] { Remote, SameNetwork }.OfType<RankedPhoneEndpoint>().ToList()
            : new[] { SameNetwork, Remote }.OfType<RankedPhoneEndpoint>().ToList();
}

/// <summary>
/// Turns the addresses this machine happens to have into an ordered recommendation.
/// </summary>
/// <remarks>
/// Pure, and separated from everything that discovers or serves, because it is the one part of this
/// feature whose behaviour is an opinion rather than a fact: it can be argued about, and it therefore has
/// to be arguable in a table test without a network card, a phone or a running server.
/// <para><b>Why a ranking at all.</b> A QR code encodes exactly one URL, and a developer machine has
/// half a dozen addresses — LAN, Tailscale, Hyper-V, WSL, Docker. Something has to choose, and the
/// signals that make the choice good are all cheap: whether the adapter has a default route (which alone
/// separates real network cards from virtual ones), whether the user is sitting at this machine or
/// connected to it remotely (which decides where their phone is), and what worked last time.</para>
/// <para><b>Why remembering is keyed by session location.</b> A single remembered winner is wrong for
/// anyone who uses one machine both ways: the machine does not change between a local day and a remote
/// one, so a global pin would have the local answer overwrite the remote one every other day. The pin is
/// per <see cref="SessionLocation"/>, and the two therefore never fight.</para>
/// </remarks>
internal static class PhoneEndpointRanker
{
    // The base scores. Spread widely enough that no accumulation of *heuristic* bonuses inverts a class:
    // the most an address can gain without having been measured is 25, which is not enough to lift one
    // with no route (20) above a working network card (80). The pin is the exception, and deliberately —
    // it is not a guess but a record of a phone actually arriving there, which is evidence that the
    // address works whatever this file believes about its adapter.
    private const int TailscaleScore = 100;
    private const int RoutedLanScore = 80;
    private const int VpnScore = 60;

    /// <summary>
    /// A name on the local network, above a numeric address that routes nowhere.
    /// </summary>
    /// <remarks>
    /// It used to sit below. That put every Hyper-V and WSL address ahead of the one entry in the
    /// fallback list that has a real chance of working — and the fallback list is only ever read by
    /// somebody whose first two codes already failed, which is the worst moment to be offering them
    /// addresses that reach nothing.
    /// </remarks>
    private const int MulticastDnsScore = 45;

    private const int UnroutedLanScore = 20;

    /// <summary>
    /// Applied to the endpoint a phone actually connected through last time, in this same kind of session.
    /// </summary>
    /// <remarks>
    /// Large enough to mean what the comment above says. At 40 it did not: after the unrouted score came
    /// down to 20, a pinned address on an adapter with no default route scored 75 against a routed card's
    /// 95, so the measurement lost to the heuristic it was supposed to overrule. Either the weight or the
    /// documentation had to give, and the weight was the one that was wrong — a pin is not a guess about
    /// an adapter, it is a record of a phone arriving, and a host that no longer exists is not a candidate
    /// at all, so a stale pin cannot promote anything.
    /// </remarks>
    private const int PinnedBonus = 100;

    /// <summary>Applied when the endpoint's audience matches where the user is sitting.</summary>
    private const int AudienceBonus = 15;

    /// <summary>Applied when the browser will accept the page without a certificate warning.</summary>
    private const int TrustedCertificateBonus = 10;

    /// <summary>
    /// Ranks <paramref name="candidates"/> and picks one recommendation per audience.
    /// </summary>
    /// <param name="location">Where the user is. <see cref="SessionLocation.Unknown"/> favours neither.</param>
    /// <param name="pinnedHost">
    /// The host a phone last reached this machine at *in this kind of session*, or null. Matched by host
    /// rather than by adapter because that is what the phone actually reported, and an adapter can hand
    /// out a different address tomorrow while still being the right adapter.
    /// </param>
    public static PhoneEndpointBoard Rank(
        IEnumerable<PhoneEndpoint> candidates,
        SessionLocation location,
        string? pinnedHost)
    {
        var preferred = location == SessionLocation.Remote
            ? PhoneEndpointAudience.Remote
            : PhoneEndpointAudience.SameNetwork;

        var ranked = candidates
            .DistinctBy(candidate => candidate.Host, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => Score(candidate, location, pinnedHost))
            .OrderByDescending(entry => entry.Score)
            // Ties broken by host so the panel does not reshuffle between two equally good addresses
            // every time it is opened — a QR code that moves while being photographed is its own bug.
            .ThenBy(entry => entry.Endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count == 0)
            return PhoneEndpointBoard.Empty;

        return new PhoneEndpointBoard(
            SameNetwork: ranked.FirstOrDefault(e => e.Audience == PhoneEndpointAudience.SameNetwork),
            Remote: ranked.FirstOrDefault(e => e.Audience == PhoneEndpointAudience.Remote),
            Preferred: preferred,
            All: ranked);
    }

    private static RankedPhoneEndpoint Score(
        PhoneEndpoint endpoint,
        SessionLocation location,
        string? pinnedHost)
    {
        var (score, reason) = endpoint.Kind switch
        {
            PhoneEndpointKind.Tailscale =>
                (TailscaleScore, "Tailscale — reaches your phone from any network"),
            PhoneEndpointKind.Vpn =>
                (VpnScore, $"VPN ({endpoint.AdapterLabel})"),
            PhoneEndpointKind.MulticastDns =>
                (MulticastDnsScore, "Local network name — survives a change of address"),
            _ when endpoint.HasDefaultGateway =>
                (RoutedLanScore, $"{endpoint.AdapterLabel} — this machine's own network"),
            _ =>
                (UnroutedLanScore, $"{endpoint.AdapterLabel} — no route off this machine"),
        };

        var pinned = pinnedHost is not null &&
                     string.Equals(pinnedHost, endpoint.Host, StringComparison.OrdinalIgnoreCase);
        if (pinned)
        {
            score += PinnedBonus;
            reason = "Worked last time — " + reason;
        }

        if (location != SessionLocation.Unknown)
        {
            var wanted = location == SessionLocation.Remote
                ? PhoneEndpointAudience.Remote
                : PhoneEndpointAudience.SameNetwork;
            if (endpoint.Audience == wanted)
                score += AudienceBonus;
        }

        if (endpoint.SupportsTrustedCertificate)
            score += TrustedCertificateBonus;

        return new RankedPhoneEndpoint(endpoint, score, reason);
    }
}
