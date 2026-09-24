using System.Net;
using System.Net.Http.Headers;

namespace mTiles.Services;

/// <summary>
/// What a refusal says about when to ask again — a 429's (or 503's) <c>Retry-After</c>, read and bounded.
/// </summary>
/// <remarks>
/// <para><b>Honoured, because ignoring it makes it longer.</b> Measured on the Claude usage endpoint: a
/// 429 carrying <c>Retry-After: 1601</c> while the tile asked every three minutes. Asking again inside
/// that window is nine more refusals, each of which a rate limiter is entitled to count against the
/// account.</para>
/// <para><b>Bounded both ways.</b> Never shorter than <paramref name="floor"/> (a <c>Retry-After: 0</c>
/// must not turn into asking on every tick) and never longer than <see cref="Cap"/>: the header is
/// somebody else's text, and a date a week out — or a clock that disagrees with ours by a day — must not
/// be able to switch an account's card off for good.</para>
/// </remarks>
public static class RetryAfter
{
    /// <summary>The longest wait a refusal can impose here.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromHours(2);

    /// <summary>When to ask again, or null when the answer carries no such request.</summary>
    /// <remarks>A 429 without the header still asks to be left alone, so it gets the floor. The floor is
    /// the caller's policy: a reader passes none and hands on only what the header said, and the service
    /// that decides how often to ask applies its own.</remarks>
    public static DateTimeOffset? From(HttpStatusCode status, RetryConditionHeaderValue? header,
        DateTimeOffset now, TimeSpan floor = default)
    {
        if (status is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return null;

        TimeSpan? wait = header switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => status == HttpStatusCode.TooManyRequests ? floor : null,
        };

        if (wait is not { } span) return null;

        return now + TimeSpan.FromTicks(Math.Clamp(span.Ticks, floor.Ticks, Cap.Ticks));
    }
}
