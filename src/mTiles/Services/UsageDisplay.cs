using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The words a usage card is written in.
/// </summary>
/// <remarks>
/// <para>Pure and separate from the tile, for the reason <see cref="ElapsedDisplay"/> is: what is
/// interesting here is not arithmetic but the shapes the sentences switch between — a reset that is due
/// today against one four days out, a pace with a projection against one without — and those are an
/// opinion, easier to argue in a table than to read off a running screen.</para>
/// <para><b>A reset is absolute and relative at once.</b> The relative half is what the reader acts on;
/// the absolute half is what survives the card being looked at again ten minutes later, when the
/// countdown has moved and nothing said so.</para>
/// </remarks>
public static class UsageDisplay
{
    /// <summary>When a window comes back, or an empty string where the service named no instant.</summary>
    /// <remarks><para>The day is named only where it is not today's, because "Wed 03:00" for three
    /// hours' time is a date where a clock time would do.</para>
    /// <para><b>Absolute and relative at once, and the word "resets" is neither.</b> The relative half is
    /// what the reader acts on and the absolute half is what survives the card being looked at again ten
    /// minutes later — but the label sits at the end of a window's own row, where what the instant is
    /// cannot be anything else. Six characters of prose per row is what pushed the figure it belongs to
    /// off the edge of a narrow tile.</para></remarks>
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } instant) return "";

        var local = instant.ToLocalTime();
        var when = local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm")
            : local.ToString("ddd HH:mm");

        var away = instant - now;
        return away <= TimeSpan.Zero ? $"resets {when} · due" : $"resets {when} · in {Rough(away)}";
    }

    /// <summary>How long until the window comes back, and nothing else.</summary>
    /// <remarks><b>The half of the reset that a glance is for.</b> The instant it happens at is the half
    /// that survives the card being looked at again ten minutes later, and it stays — in the row's
    /// tooltip, with the whole sentence. On the row itself it was six characters of prose and a clock
    /// time competing with the figure they qualify, on every window of every account.</remarks>
    public static string Countdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } instant) return "";

        var away = instant - now;
        return away <= TimeSpan.Zero ? "due" : Rough(away);
    }

    /// <summary>What the pace amounts to, in words, or an empty string where there is no pace.</summary>
    /// <remarks>The projection is named only when there is one: a window spending slowly enough to
    /// outlast its own reset has nothing to warn about, and <c>UsagePace</c> answers null for it rather
    /// than a date.</remarks>
    public static string Pace(UsagePace pace, DateTimeOffset now)
    {
        if (pace.DeltaPoints is not { } delta) return "";

        var points = Math.Abs(Math.Round(delta));
        var verdict = pace.State switch
        {
            UsagePaceState.Ahead => $"{points:0} points ahead of the clock",
            UsagePaceState.Behind => $"{points:0} points spare",
            _ => "on pace",
        };

        return pace.EmptyAt is { } empty
            ? $"{verdict} — empty by {Moment(empty, now)}"
            : verdict;
    }

    /// <summary>How old a reading is, where that is worth saying.</summary>
    /// <remarks>Only where it is older than the window it describes: figures a minute old are simply
    /// the figures, and stamping every card with an age would bury the one stamp that matters —
    /// codex's, which is as fresh as its last reply and no fresher.</remarks>
    public static string Age(AiUsageReport report, DateTimeOffset now)
    {
        var shortest = report.Windows
            .Where(window => window.Length > TimeSpan.Zero)
            .Select(window => window.Length)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Min();

        var age = now - report.MeasuredAt;
        return shortest > TimeSpan.Zero && age > shortest ? $"{Rough(age)} old" : "";
    }

    /// <summary>An amount with the service's own symbol in front of it.</summary>
    /// <remarks>Null answers an empty string and never <c>0</c>, the rule the whole of
    /// <see cref="AiUsageWindow"/> is built on: a figure the service did not state, drawn as zero, tells
    /// a user whose key works that they have run out.</remarks>
    public static string Money(decimal? amount, string? currency) =>
        amount is { } value ? $"{currency}{value:0.00}" : "";

    /// <summary>A span written the way a card says it — one unit, two at most, and never seconds.</summary>
    private static string Rough(TimeSpan span)
    {
        var total = span.Duration();

        if (total.TotalDays >= 1) return $"{(int)total.TotalDays}d {total.Hours}h";
        if (total.TotalHours >= 1) return $"{(int)total.TotalHours}h {total.Minutes}m";
        return $"{Math.Max((int)total.TotalMinutes, 1)}m";
    }

    /// <summary>An instant written as a clock time today and as a day and time otherwise.</summary>
    private static string Moment(DateTimeOffset instant, DateTimeOffset now)
    {
        var local = instant.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }
}
