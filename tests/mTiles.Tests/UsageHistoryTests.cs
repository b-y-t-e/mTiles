using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The seven bars, which are this application's own snapshots and nobody else's.
/// </summary>
/// <remarks>
/// Nobody publishes a per-day history for an ordinary key — OpenRouter's <c>api/v1/activity</c> answers
/// 403 — so the rules that keep these honest are all here: a day nobody sampled is null and not zero, a
/// poll landing after midnight cannot overwrite a finished day, and an unreadable file is a fresh start
/// rather than a crash.
/// </remarks>
public class UsageHistoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mtiles-usage-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_directory, "history.json");

    private static readonly DateTimeOffset Noon = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A temporary directory that will not go is not a failing test. */ }
    }

    [Fact]
    public void ADayIsRecordedAndReadBack()
    {
        var history = new UsageHistory(FilePath);
        history.Record("openrouter:one", Noon, 4.18m);

        var days = history.Days("openrouter:one", Noon, 7);

        Assert.Equal(7, days.Count);
        Assert.Equal(4.18m, days[^1].Amount);
        Assert.Equal(new DateOnly(2026, 9, 1), days[^1].Date);
    }

    /// <summary>A day nobody sampled and a day that cost nothing are not the same fact.</summary>
    [Fact]
    public void ADayNothingWasRecordedForIsNullRatherThanZero()
    {
        var history = new UsageHistory(FilePath);
        history.Record("openrouter:one", Noon, 4.18m);

        Assert.All(history.Days("openrouter:one", Noon, 7).SkipLast(1),
            day => Assert.Null(day.Amount));
    }

    /// <summary>The value is a running daily total, so the largest reading for a date is the day's.</summary>
    [Fact]
    public void TheMaximumForADateWins()
    {
        var history = new UsageHistory(FilePath);
        history.Record("openrouter:one", Noon, 9m);
        history.Record("openrouter:one", Noon.AddHours(1), 2m);

        Assert.Equal(9m, history.Days("openrouter:one", Noon, 1)[0].Amount);
    }

    /// <summary>UTC, because that is the boundary the counter being sampled resets on.</summary>
    [Fact]
    public void TheDayBoundaryIsUtc()
    {
        var history = new UsageHistory(FilePath);
        var lateOnTheFirst = new DateTimeOffset(2026, 9, 1, 23, 30, 0, TimeSpan.Zero);
        var earlyOnTheSecond = new DateTimeOffset(2026, 9, 2, 0, 30, 0, TimeSpan.Zero);

        history.Record("openrouter:one", lateOnTheFirst, 9m);
        history.Record("openrouter:one", earlyOnTheSecond, 0.5m);

        var days = history.Days("openrouter:one", earlyOnTheSecond, 2);

        Assert.Equal(9m, days[0].Amount);
        Assert.Equal(0.5m, days[1].Amount);
    }

    [Fact]
    public void DaysOlderThanTheRetentionWindowArePruned()
    {
        var history = new UsageHistory(FilePath);
        history.Record("openrouter:one", Noon.AddDays(-UsageHistory.RetainedDays - 1), 9m);
        history.Record("openrouter:one", Noon, 1m);

        Assert.Equal(new DateOnly(2026, 9, 1), history.CollectingSince("openrouter:one"));
    }

    [Fact]
    public void TheOldestRecordedDayIsWhatCollectingSinceAnswers()
    {
        var history = new UsageHistory(FilePath);
        history.Record("openrouter:one", Noon.AddDays(-3), 1m);
        history.Record("openrouter:one", Noon, 2m);

        Assert.Equal(new DateOnly(2026, 8, 29), history.CollectingSince("openrouter:one"));
    }

    [Fact]
    public void ASourceNothingWasRecordedForIsCollectingSinceNever() =>
        Assert.Null(new UsageHistory(FilePath).CollectingSince("openrouter:one"));

    /// <summary>What is lost is a row of bars; the card is still worth drawing without them.</summary>
    [Fact]
    public void AnUnreadableFileIsAFreshStartRatherThanACrash()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var history = new UsageHistory(FilePath);

        Assert.All(history.Days("openrouter:one", Noon, 7), day => Assert.Null(day.Amount));
        Assert.True(history.Record("openrouter:one", Noon, 1m));
    }

    [Fact]
    public void RecordingIsPersistedAcrossInstances()
    {
        new UsageHistory(FilePath).Record("openrouter:one", Noon, 4.18m);

        Assert.Equal(4.18m, new UsageHistory(FilePath).Days("openrouter:one", Noon, 1)[0].Amount);
    }
}
