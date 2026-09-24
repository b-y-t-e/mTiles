using System.Net.Http.Headers;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A rate-limited account keeps its card: it is not asked again before the service allows, its last
/// good reading stands in for as long as that takes, and that reading survives a restart.
/// </summary>
public class UsageRateLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 15, 0, 0, TimeSpan.Zero);

    private static AiUsageReport Good(DateTimeOffset at) =>
        new("claude:sub1", "Claude Code · Sub1", "Pro",
            [new AiUsageWindow("5h", TimeSpan.FromHours(5), UsedPercent: 10, ResetsAt: at.AddHours(3))],
            RemainingCredit: null, Currency: null, at, Problem: null);

    private static AiUsageReport Limited(DateTimeOffset at, TimeSpan wait) =>
        AiUsageReport.Failed("claude:sub1", "Claude Code · Sub1", "rate-limited", at, at + wait);

    private sealed class Source(AiUsageReport? answer) : IUsageSource
    {
        public string Id => "claude:sub1";
        public AiUsageReport? Answer { get; set; } = answer;
        public int Calls { get; private set; }

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"mtiles-usage-{Guid.NewGuid():N}", "last-readings.json");

    [Fact]
    public async Task A_rate_limited_account_is_not_asked_before_it_is_allowed()
    {
        var source = new Source(Good(Now));
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        now += AiUsageService.RefreshInterval;
        source.Answer = Limited(now, TimeSpan.FromMinutes(27));
        await service.RefreshAsync(force: true);
        Assert.Equal(2, source.Calls);

        now += AiUsageService.RefreshInterval;
        await service.RefreshAsync(force: true);

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_rate_limited_account_keeps_its_reading_past_the_mask_limit()
    {
        var good = Good(Now);
        var source = new Source(good);
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        now += AiUsageService.RefreshInterval;
        source.Answer = Limited(now, AiUsageService.MaskLimit * 2);
        await service.RefreshAsync(force: true);

        now += AiUsageService.MaskLimit;
        await service.RefreshAsync(force: true);

        Assert.Equal(good, Assert.Single(service.Reports));
    }

    [Fact]
    public async Task A_reading_survives_a_restart_into_a_rate_limit()
    {
        var file = TempFile();
        var good = Good(Now);
        var first = new AiUsageService(new SettingsService(), null, _ => [new Source(good)], () => Now,
            new UsageLastReadings(file));
        await first.RefreshAsync(force: true);
        first.Dispose();

        var later = Now + TimeSpan.FromMinutes(5);
        var source = new Source(Limited(later, TimeSpan.FromMinutes(27)));
        var second = new AiUsageService(new SettingsService(), null, _ => [source], () => later,
            new UsageLastReadings(file));
        await second.RefreshAsync(force: true);

        var shown = Assert.Single(second.Reports);
        Assert.True(shown.Answered);
        Assert.Equal(good.Windows[0].UsedPercent, shown.Windows[0].UsedPercent);
    }

    [Fact]
    public void Retry_after_is_read_as_a_delta_or_a_date_and_never_as_nothing()
    {
        Assert.Equal(TimeSpan.FromSeconds(1601),
            ClaudeUsageReader.RetryAfterOf(new RetryConditionHeaderValue(TimeSpan.FromSeconds(1601)), Now));
        Assert.Equal(TimeSpan.FromMinutes(5),
            ClaudeUsageReader.RetryAfterOf(new RetryConditionHeaderValue(Now.AddMinutes(5)), Now));
        Assert.True(ClaudeUsageReader.RetryAfterOf(null, Now) > TimeSpan.Zero);
    }
}
