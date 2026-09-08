using mTiles.Models;

namespace mTiles.Services.Activity;

/// <summary>What one round of arbitration decided, and what it is still waiting on.</summary>
/// <param name="PendingSince">When the fall away from <see cref="TileActivity.Working"/> or
/// <see cref="TileActivity.Blocked"/> began, or null when nothing is pending. Held by the caller and
/// handed back, which is what keeps <see cref="ActivityPolicy"/> a function.</param>
public readonly record struct ActivityDecision(TileActivity State, DateTime? PendingSince);

/// <summary>
/// The rules a tile's activity is decided by: which source wins, how long each one's answer counts for,
/// and how long the light stays on after the last of them stops saying so.
/// </summary>
/// <remarks>
/// <para>Pure, and separate from the loop that carries it out — the same construction as
/// <c>ChainPolicy</c> and <c>UsagePace</c>, and for the same reason: three rules that are each an
/// opinion, readable in a table test without a terminal, a dispatcher or a sleep.</para>
/// <para><b>The confirmation window is not flicker-smoothing.</b> It is the only defence against a tile
/// stuck on "working" for the rest of the session. Every tool that has tried to read an agent's state
/// reports the same asymmetry: idle&#8594;working is caught reliably and working&#8594;idle is the
/// transition that gets missed, so the state that has to be <em>proved</em> is the one that turns the
/// light off. Removing this because the picture looks steady enough without it is the change that
/// brings the bug back — and it will look steady, because the failure is a tile nobody is watching any
/// more.</para>
/// </remarks>
public static class ActivityPolicy
{
    /// <summary>How long the answer "not working" has to hold before the light goes out.</summary>
    public static readonly TimeSpan IdleConfirmation = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a source's last answer still counts for.
    /// </summary>
    /// <remarks>Per authority, because they say different kinds of thing. Output is a symptom and
    /// decays with <see cref="ActivityWindow.DefaultWindow"/>, where that smoothing is argued. A title
    /// is re-asserted while it means something and stops when the CLI exits, so it is given room to be
    /// slow. A lifecycle report is a <em>statement</em> — a <c>Stop</c> is not sent twice — so it
    /// stands until something contradicts it, and it has a window at all only so that a crashed agent's
    /// last word does not stand for the rest of the session.</remarks>
    public static TimeSpan FreshnessOf(ActivityAuthority authority) => authority switch
    {
        ActivityAuthority.Lifecycle => TimeSpan.FromMinutes(30),
        ActivityAuthority.Osc => TimeSpan.FromSeconds(20),
        ActivityAuthority.Screen => TimeSpan.FromSeconds(15),
        _ => ActivityWindow.DefaultWindow,
    };

    /// <summary>
    /// Weighs the readings and answers what the tile is doing now.
    /// </summary>
    /// <param name="readings">One per source, in any order. A source that has never answered is simply
    /// absent.</param>
    /// <param name="now">The clock, passed in.</param>
    /// <param name="current">What the tile is showing at the moment.</param>
    /// <param name="pendingSince">What the previous round handed back.</param>
    public static ActivityDecision Decide(
        IReadOnlyList<ActivityReading> readings,
        DateTime now,
        TileActivity current,
        DateTime? pendingSince)
    {
        var candidate = Candidate(readings, now);

        // Nothing to decide — and this is also what clears a pending fall: a candidate that has come
        // back to what is already on screen was never a transition.
        if (candidate == current) return new ActivityDecision(current, null);

        // Up is immediate. Being late to say "working" is a tile that looks asleep while it builds, and
        // there is nothing to prove here — something answered.
        if (candidate is TileActivity.Working or TileActivity.Blocked)
            return new ActivityDecision(candidate, null);

        // Down has to be proved, but only from a state that was claiming work. Unknown to Idle and Idle
        // to Unknown are both "still nothing", and making anyone wait two seconds for one kind of
        // nothing to become another is a delay with nothing behind it.
        if (current is not (TileActivity.Working or TileActivity.Blocked))
            return new ActivityDecision(candidate, null);

        if (pendingSince is not { } began) return new ActivityDecision(current, now);
        return now - began >= IdleConfirmation
            ? new ActivityDecision(candidate, null)
            : new ActivityDecision(current, began);
    }

    /// <summary>
    /// The state of the highest-ranked source that is both fresh and willing to answer; ties within a
    /// rank go to whichever spoke last.
    /// </summary>
    /// <remarks><b>A source answering <see cref="TileActivity.Unknown"/> is not answering</b>, so it
    /// silences nothing below it. That is the difference between "the agent reports it is idle" and "no
    /// rule matched this screen", and only the first may put a light out.</remarks>
    private static TileActivity Candidate(IReadOnlyList<ActivityReading> readings, DateTime now)
    {
        ActivityReading? best = null;
        foreach (var reading in readings)
        {
            if (reading.State == TileActivity.Unknown) continue;
            if (now - reading.At >= FreshnessOf(reading.Authority)) continue;
            if (best is { } held && !Beats(reading, held)) continue;
            best = reading;
        }
        return best?.State ?? TileActivity.Unknown;
    }

    private static bool Beats(ActivityReading candidate, ActivityReading held) =>
        candidate.Authority != held.Authority
            ? candidate.Authority > held.Authority
            : candidate.At > held.At;

    /// <summary>
    /// The sentence belonging to whatever <see cref="Decide"/> settled on, or null.
    /// </summary>
    /// <remarks>Asked separately rather than carried out of the decision, because the decision can be
    /// the <em>previous</em> state held through a pending fall — and the sentence that went with it is
    /// still the right one to show while it is being held.</remarks>
    public static string? DetailFor(
        IReadOnlyList<ActivityReading> readings, DateTime now, TileActivity state)
    {
        if (state is TileActivity.Unknown) return null;

        ActivityReading? best = null;
        foreach (var reading in readings)
        {
            if (reading.State != state || reading.Detail is null) continue;
            if (now - reading.At >= FreshnessOf(reading.Authority)) continue;
            if (best is { } held && !Beats(reading, held)) continue;
            best = reading;
        }
        return best?.Detail;
    }
}
