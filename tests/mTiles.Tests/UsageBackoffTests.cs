using System.Net;
using System.Net.Http.Headers;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A refusal that says when to come back: the account is not asked again before then, its last good
/// reading stays on the card — stamped with its age — and both survive a restart.
/// </summary>
/// <remarks>Measured on the Claude usage endpoint: <c>Retry-After: 1601</c> while the tile asked every
/// three minutes, so the card vanished after <c>MaskLimit</c> and every further question was another
/// refusal against the account.</remarks>
public sealed class UsageBackoffTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mtiles-usage-backoff-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private UsageSnapshots Snapshots() => new(Path.Combine(_directory, "last.json"));

    private static AiUsageReport Good() =>
        new("claude", "Claude", "Max",
            [new AiUsageWindow("5h", TimeSpan.FromHours(5), UsedPercent: 40, ResetsAt: Now.AddHours(2))],
            RemainingCredit: null, Currency: null, Now, Problem: null);

    private static AiUsageReport RateLimited(DateTimeOffset until) =>
        AiUsageReport.Failed("claude", "Claude", "rate-limited", Now, until);

    private sealed class Source(AiUsageReport? answer) : IUsageSource
    {
        public string Id => "claude";
        public AiUsageReport? Answer { get; set; } = answer;
        public int Calls { get; private set; }

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task A_refused_account_is_not_asked_again_before_the_time_it_named()
    {
        var source = new Source(Good());
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        now += AiUsageService.RefreshInterval;
        source.Answer = RateLimited(now + TimeSpan.FromSeconds(1601));
        await service.RefreshAsync(force: true);
        Assert.Equal(2, source.Calls);

        // Well past the 3-minute window and past MaskLimit, still inside the 1601 seconds.
        for (var i = 0; i < 8; i++)
        {
            now += AiUsageService.RefreshInterval;
            await service.RefreshAsync(force: true);
        }

        Assert.Equal(2, source.Calls);
        var shown = Assert.Single(service.Reports);
        Assert.True(shown.Answered);
        Assert.True(shown.HeldOver);
        Assert.NotEqual("", UsageDisplay.Age(shown, now));

        now = Now + AiUsageService.RefreshInterval + TimeSpan.FromSeconds(1602);
        source.Answer = Good();
        await service.RefreshAsync(force: true);

        Assert.Equal(3, source.Calls);
        Assert.False(Assert.Single(service.Reports).HeldOver);
    }

    [Fact]
    public async Task A_reading_and_a_refusal_survive_a_restart()
    {
        var source = new Source(Good());
        var now = Now;
        var first = new AiUsageService(new SettingsService(), null, _ => [source], () => now, Snapshots());

        await first.RefreshAsync(force: true);
        now += AiUsageService.RefreshInterval;
        source.Answer = RateLimited(now + TimeSpan.FromMinutes(20));
        await first.RefreshAsync(force: true);
        Assert.Equal(2, source.Calls);

        now += TimeSpan.FromMinutes(1);
        var second = new AiUsageService(new SettingsService(), null, _ => [source], () => now, Snapshots());
        await second.RefreshAsync(force: true);

        Assert.Equal(2, source.Calls);
        var shown = Assert.Single(second.Reports);
        Assert.True(shown.HeldOver);
        Assert.Equal(40, shown.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task A_restored_reading_with_no_refusal_is_asked_for_at_once()
    {
        var source = new Source(Good());
        var now = Now;
        var first = new AiUsageService(new SettingsService(), null, _ => [source], () => now, Snapshots());
        await first.RefreshAsync(force: true);

        var second = new AiUsageService(new SettingsService(), null, _ => [source], () => now, Snapshots());
        await second.RefreshAsync(force: true);

        Assert.Equal(2, source.Calls);
    }

    public static TheoryData<HttpStatusCode, string?, TimeSpan?> Refusals => new()
    {
        // Seconds, as measured.
        { HttpStatusCode.TooManyRequests, "1601", TimeSpan.FromSeconds(1601) },
        // No header still asks to be left alone: the floor.
        { HttpStatusCode.TooManyRequests, null, AiUsageService.RefreshInterval },
        // Zero must not become asking on every tick.
        { HttpStatusCode.TooManyRequests, "0", AiUsageService.RefreshInterval },
        // Somebody else's text cannot switch a card off for a day.
        { HttpStatusCode.TooManyRequests, "86400", RetryAfter.Cap },
        { HttpStatusCode.ServiceUnavailable, "600", TimeSpan.FromMinutes(10) },
        // A 503 without the header is an ordinary failure.
        { HttpStatusCode.ServiceUnavailable, null, null },
        // Anything else carries no such request.
        { HttpStatusCode.Unauthorized, "600", null },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Retry_after_is_read_and_bounded(HttpStatusCode status, string? header, TimeSpan? expected)
    {
        var value = header is null ? null : RetryConditionHeaderValue.Parse(header);

        var until = RetryAfter.From(status, value, Now, AiUsageService.RefreshInterval);

        Assert.Equal(expected is { } wait ? Now + wait : null, until);
    }

    [Fact]
    public void Retry_after_as_a_date_is_measured_from_now()
    {
        var value = new RetryConditionHeaderValue(Now.AddMinutes(25));

        Assert.Equal(Now.AddMinutes(25),
            RetryAfter.From(HttpStatusCode.TooManyRequests, value, Now, AiUsageService.RefreshInterval));
    }
}
