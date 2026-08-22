using System.Diagnostics;

namespace mTiles.Services.Phone;

/// <summary>
/// Collects candidate addresses from every <see cref="IPhoneEndpointSource"/> and ranks them.
/// </summary>
/// <remarks>
/// The composition root for the address half of the feature: the only place that knows which sources
/// exist. Adding a way of reaching this machine means adding a source to the list handed to the
/// constructor — the ranking, the server and the panel are untouched, because none of them enumerates
/// kinds.
/// </remarks>
internal sealed class PhoneEndpointDirectory(
    IReadOnlyList<IPhoneEndpointSource> sources,
    ISessionLocationProbe locationProbe)
{
    /// <summary>The set used by the application: adapters, Tailscale, and the mDNS name.</summary>
    public static PhoneEndpointDirectory CreateDefault() => new(
        [new NetworkEndpointSource(), new TailscaleEndpointSource(), new MulticastDnsEndpointSource()],
        new SessionLocationProbe());

    public SessionLocation Location => locationProbe.Current;

    /// <summary>
    /// Everything every source can see. A source that throws contributes nothing and is logged: one
    /// misbehaving lookup must not cost the user every other address on the machine.
    /// </summary>
    public IReadOnlyList<PhoneEndpoint> Discover()
    {
        var found = new List<PhoneEndpoint>();

        foreach (var source in sources)
        {
            try
            {
                found.AddRange(source.Discover());
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Phone endpoint source '{0}' failed: {1}", source.Name, ex.Message);
            }
        }

        return found;
    }

    /// <summary>
    /// Discovers and ranks in one step.
    /// </summary>
    /// <param name="pinnedHostFor">
    /// What connected last time, asked per session location. A function rather than a value because the
    /// location is decided here, and the caller must not be able to answer for the wrong one — that
    /// mismatch is exactly the bug the per-location pin exists to prevent.
    /// </param>
    public PhoneEndpointBoard Build(Func<SessionLocation, string?> pinnedHostFor)
    {
        var location = locationProbe.Current;
        return PhoneEndpointRanker.Rank(Discover(), location, pinnedHostFor(location));
    }
}
