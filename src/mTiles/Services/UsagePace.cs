using mTiles.Models;

namespace mTiles.Services;

/// <summary>Whether a window is being spent faster than it is passing.</summary>
public enum UsagePaceState
{
    /// <summary>Not enough was said to work it out — no reset instant, no length, or no percentage.</summary>
    Unknown,

    /// <summary>Less spent than the elapsed share of the window.</summary>
    Behind,

    /// <summary>Within the dead band either side of the elapsed share.</summary>
    OnPace,

    /// <summary>More spent than the elapsed share, which is the state worth acting on.</summary>
    Ahead,
}

/// <summary>
/// How far through a limit window the spending is, against how far through the window the clock is.
/// </summary>
/// <remarks>
/// <para><b>Elapsed time is derived from <c>ResetsAt - Length</c>, never from the day of the week.</b>
/// Claude's and codex's seven-day windows roll, so "it is Wednesday, therefore 43% of the week is gone"
/// is wrong by up to a day — and the same subtraction then serves the five-hour window for free, where a
/// weekday would mean nothing at all.</para>
/// <para><b>Three states with a dead band, not two.</b> Without the band the label flips between two
/// words on every refresh for an account that is spending exactly on pace, which is the one account
/// there is nothing to say about.</para>
/// <para>Pure and clock-injected, so the whole of it is readable in a table test: the interesting cases
/// are a window that has just reset, one about to, and one already past its limit, and none of those can
/// be reached by waiting.</para>
/// </remarks>
/// <param name="State">The verdict.</param>
/// <param name="ExpectedPercent">The share of the window the clock has used, 0..100, or null when it
/// could not be worked out.</param>
/// <param name="DeltaPoints">Percentage points spent above the expected share — negative when
/// under — or null when it could not be worked out.</param>
/// <param name="EmptyAt">When the window would be exhausted if spending carried on at the rate so far,
/// or null when there is no rate to project from.</param>
public sealed record UsagePace(
    UsagePaceState State,
    double? ExpectedPercent,
    double? DeltaPoints,
    DateTimeOffset? EmptyAt)
{
    /// <summary>How far either side of the expected share still counts as on pace.</summary>
    /// <remarks>Percentage points, and deliberately generous: the figures are refreshed every few
    /// minutes and the window keeps moving underneath them, so a narrower band would report a change
    /// that is the clock ticking rather than anything the user did.</remarks>
    public const double DeadBandPoints = 3;

    /// <summary>What nothing could be said about.</summary>
    public static readonly UsagePace Unknown = new(UsagePaceState.Unknown, null, null, null);

    /// <summary>The pace of one window as of <paramref name="now"/>.</summary>
    public static UsagePace For(AiUsageWindow window, DateTimeOffset now)
    {
        if (window.UsedPercent is not { } used
            || window.ResetsAt is not { } resets
            || window.Length <= TimeSpan.Zero)
            return Unknown;

        var elapsed = Clamp(window.Length - (resets - now), TimeSpan.Zero, window.Length);
        var expected = elapsed / window.Length * 100;
        var delta = used - expected;

        return new UsagePace(StateFor(delta), expected, delta, Projection(used, elapsed, window.Length, now));
    }

    private static UsagePaceState StateFor(double delta) =>
        Math.Abs(delta) <= DeadBandPoints ? UsagePaceState.OnPace
        : delta > 0 ? UsagePaceState.Ahead
        : UsagePaceState.Behind;

    /// <summary>
    /// When the allowance runs out at the rate observed so far.
    /// </summary>
    /// <remarks><para>Only where there is a rate: a window that has just reset has spent nothing over no
    /// time, and dividing by either of those produces an instant that is either infinite or meaningless.
    /// A window already at or past its limit is empty now, which is the honest answer rather than an
    /// instant in the past.</para>
    /// <para><b>A projection that lands past the end of the window is no projection at all</b> and is
    /// answered null rather than as a date: at that rate the allowance outlasts its own reset, so there
    /// is nothing to warn about — and it is what keeps a rate of almost nothing from overflowing a
    /// <see cref="TimeSpan"/> on its way to a date in the year 40 000.</para></remarks>
    private static DateTimeOffset? Projection(double used, TimeSpan elapsed, TimeSpan length,
        DateTimeOffset now)
    {
        if (used <= 0 || elapsed <= TimeSpan.Zero) return null;
        if (used >= 100) return now;

        // The comparison is made on the factor, before anything is multiplied: for a rate of almost
        // nothing the factor runs into the thousands and `elapsed * factor` throws OverflowException
        // rather than producing the long TimeSpan the test below would then reject.
        var factor = (100 - used) / used;
        var fits = (length - elapsed) / elapsed;
        if (factor > fits) return null;

        return now + elapsed * factor;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) =>
        value < low ? low : value > high ? high : value;
}
