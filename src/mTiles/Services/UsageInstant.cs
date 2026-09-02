using System.Text.Json;

namespace mTiles.Services;

/// <summary>
/// Reads the moment a limit window comes back, in whichever of the three shapes a service states it.
/// </summary>
/// <remarks>
/// <para>One rule rather than one per reader, because the shapes are not a matter of taste and each
/// reader would otherwise learn them separately: Anthropic answers an ISO instant, codex a unix second,
/// and both have been seen stating seconds-from-now instead. Two copies of a tolerant read are two
/// places for a service's change to be handled differently.</para>
/// <para><b>Anything unrecognised is <c>null</c>, never <see cref="DateTimeOffset.MinValue"/> and never
/// "now".</b> A reset instant is what the pace and the countdown are worked out from, so a wrong one is
/// a card confidently reporting a week that ended in 1601.</para>
/// </remarks>
public static class UsageInstant
{
    /// <summary>The point after which a number is an absolute unix second rather than a duration.</summary>
    /// <remarks>Roughly the year 2001. No limit window is 31 million seconds long and no reset lands
    /// before this application existed, so the two readings cannot be confused in any case that
    /// occurs.</remarks>
    private const long UnixSecondsFrom = 1_000_000_000;

    /// <summary>The instant a named field states, or null when it states none this can read.</summary>
    public static DateTimeOffset? From(JsonElement owner, string field, DateTimeOffset? now = null) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(field, out var value)
            ? From(value, now)
            : null;

    /// <summary>The instant one value states, or null.</summary>
    public static DateTimeOffset? From(JsonElement value, DateTimeOffset? now = null) => value.ValueKind switch
    {
        JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), out var instant) => instant,
        JsonValueKind.Number when value.TryGetInt64(out var seconds) => FromSeconds(seconds, now),
        _ => null,
    };

    /// <summary>A unix second read as itself, and a small number read as a countdown from
    /// <paramref name="now"/>.</summary>
    private static DateTimeOffset? FromSeconds(long seconds, DateTimeOffset? now) =>
        seconds >= UnixSecondsFrom ? DateTimeOffset.FromUnixTimeSeconds(seconds)
        : seconds > 0 ? (now ?? DateTimeOffset.Now) + TimeSpan.FromSeconds(seconds)
        : null;
}
