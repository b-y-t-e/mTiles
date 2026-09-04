using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The usage tile: what it is built from, what it writes down, and what it does when nobody answers.
/// </summary>
/// <remarks>
/// The behaviour worth pinning is negative on both sides. It saves <c>null</c>, because it holds no work
/// — and a kind that started writing state would be one a rolled-back build then read as an unknown
/// shape. And an account that could not be asked keeps its card and its sentence, where an account that
/// does not exist here has no card at all: those two are the whole point of the tile, and drawn alike
/// they are a zero on screen for a subscription that works perfectly well.
/// </remarks>
public class UsageTileTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static AiUsageService ServiceOf(params AiUsageReport?[] answers) =>
        new(new SettingsService(), new UsageHistory(
                Path.Combine(Path.GetTempPath(), $"mtiles-usage-{Guid.NewGuid():N}", "history.json")),
            _ => [.. answers.Select(answer => new StubSource(answer))]);

    private sealed class StubSource(AiUsageReport? answer) : IUsageSource
    {
        public string Id { get; } = Guid.NewGuid().ToString();

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(answer);
    }

    private static AiUsageReport Subscription(string id, double usedPercent) =>
        new(id, id, "Max",
            [new AiUsageWindow("5h", TimeSpan.FromHours(5), UsedPercent: usedPercent,
                ResetsAt: Now.AddHours(2))],
            RemainingCredit: null, Currency: null, Now, Problem: null);

    [Fact]
    public void The_kind_saves_nothing_because_it_holds_nothing()
    {
        var kind = (ITileKind)new UsageTileKind(ServiceOf());
        var tile = kind.Create(new TileContext(Path.GetTempPath(), new SettingsService()), null);

        Assert.Equal(TileKindIds.Usage, tile.KindId);
        Assert.Null(kind.Save(tile));

        tile.Dispose();
    }

    /// <summary>A layout written by a build that has this kind, read by one that does not, opens the
    /// leaf as an empty tile — which costs a click, where pretending it is a terminal would open a shell
    /// nobody asked for.</summary>
    [Fact]
    public void The_kind_has_no_legacy_name() =>
        Assert.Null(TileKindIds.ToLegacy(TileKindIds.Usage));

    [Fact]
    public async Task An_account_that_answers_nothing_gets_no_card()
    {
        var service = ServiceOf(null, null);

        await service.RefreshAsync(force: true);

        Assert.Empty(service.Reports);
    }

    /// <summary>The distinction the whole tile rests on: a failure keeps its card, so the sentence has
    /// somewhere to stand - but the tile draws no card for it, which is the user's call: most of these
    /// failures are an account they do not reach through this machine, and a dashboard whose permanent
    /// top line is a sentence about one of them is a dashboard they stop reading.</summary>
    [Fact]
    public async Task An_account_that_could_not_be_asked_gets_no_card()
    {
        var service = ServiceOf(AiUsageReport.Failed("claude:work", "Claude Code - work",
            "Nobody is signed in here.", Now));

        await service.RefreshAsync(force: true);

        var report = Assert.Single(service.Reports);
        Assert.False(report.Answered);

        var tile = new UsageTileViewModel(service);
        Assert.Empty(tile.Accounts);
        tile.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task A_second_refresh_inside_the_window_asks_nobody_again()
    {
        var source = new CountingSource();
        var service = new AiUsageService(new SettingsService(), null, _ => [source]);

        await service.RefreshAsync(force: true);
        await service.RefreshAsync();

        Assert.Equal(1, source.Calls);
    }

    /// <summary>The manual button no longer breaks a single account's own 3-minute window: two presses
    /// in a row, with no time passing between them, ask that account once.</summary>
    [Fact]
    public async Task The_manual_refresh_asks_again()
    {
        var source = new CountingSource();
        var service = new AiUsageService(new SettingsService(), null, _ => [source]);

        await service.RefreshAsync(force: true);
        await service.RefreshAsync(force: true);

        Assert.Equal(1, source.Calls);
    }

    /// <summary>An account that has gone stale — its own window elapsed, however the round was started
    /// — is asked again, manual button included.</summary>
    [Fact]
    public async Task The_manual_refresh_asks_again_once_the_source_is_stale()
    {
        var source = new CountingSource();
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        now += AiUsageService.RefreshInterval + TimeSpan.FromSeconds(1);
        await service.RefreshAsync(force: true);

        Assert.Equal(2, source.Calls);
    }

    /// <summary>A source still inside its own window is not asked at all — not even once — whatever
    /// started the round.</summary>
    [Fact]
    public async Task A_source_inside_its_own_window_is_never_asked_twice()
    {
        var source = new CountingSource();
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        now += TimeSpan.FromMinutes(1);
        await service.RefreshAsync(force: true);

        Assert.Equal(1, source.Calls);
    }

    /// <summary>A failed attempt following a good one keeps the last good report on screen rather than
    /// clearing the card or replacing it with a bare sentence.</summary>
    [Fact]
    public async Task A_failed_attempt_after_a_good_one_keeps_the_last_good_report()
    {
        var good = Subscription("codex", 12);
        var source = new ChangingSource(good);
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);
        Assert.Equal(good, Assert.Single(service.Reports));

        source.Answer = null;
        now += AiUsageService.RefreshInterval + TimeSpan.FromSeconds(1);
        await service.RefreshAsync(force: true);

        Assert.Equal(good, Assert.Single(service.Reports));
    }

    /// <summary>Holding a good reading over is for a bad round, not for an account that has stopped
    /// answering: past the mask limit the stale figures give way to the account's own report.</summary>
    [Fact]
    public async Task A_reading_older_than_the_mask_limit_stops_standing_in()
    {
        var good = Subscription("codex", 12);
        var source = new ChangingSource(good);
        var now = Now;
        var service = new AiUsageService(new SettingsService(), null, _ => [source], () => now);

        await service.RefreshAsync(force: true);

        source.Answer = null;

        // Every round from here on fails; the good reading stands in until it is older than the limit.
        for (var elapsed = TimeSpan.Zero; elapsed < AiUsageService.MaskLimit;
             elapsed += AiUsageService.RefreshInterval)
        {
            now += AiUsageService.RefreshInterval;
            await service.RefreshAsync(force: true);
        }

        Assert.Empty(service.Reports);
    }

    /// <summary>A source whose answer can be changed between rounds, with a stable identity across
    /// them — what the per-source cache is keyed on.</summary>
    private sealed class ChangingSource(AiUsageReport? answer) : IUsageSource
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public AiUsageReport? Answer { get; set; } = answer;

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(Answer);
    }

    /// <summary>A source that breaks the "never throws" contract must not cost the other cards their
    /// refresh.</summary>
    [Fact]
    public async Task A_source_that_throws_costs_only_its_own_card()
    {
        var service = new AiUsageService(new SettingsService(), null,
            _ => [new ThrowingSource(), new StubSource(Subscription("codex", 12))]);

        await service.RefreshAsync(force: true);

        Assert.Equal("codex", Assert.Single(service.Reports).SourceId);
    }

    private sealed class CountingSource : IUsageSource
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public int Calls { get; private set; }

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<AiUsageReport?>(null);
        }
    }

    private sealed class ThrowingSource : IUsageSource
    {
        public string Id { get; } = Guid.NewGuid().ToString();

        public Task<AiUsageReport?> ReadAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("somebody else's CLI");
    }

    /// <summary>A card draws the figures it was given and never converts one kind into the other.</summary>
    [Fact]
    public void A_percentage_card_shows_bars_and_no_money()
    {
        var card = new UsageAccountViewModel(Subscription("claude", 60), Now);

        Assert.True(card.HasLineItems);
        Assert.False(card.HasRemaining);
        Assert.False(card.HasMoney);
        // Nothing to say about a balance is nothing on the line: the items are the windows themselves.
        Assert.Equal(card.Windows.Count, card.LineItems.Count);
        Assert.Equal("60%", card.Windows[0].PercentLabel);
        Assert.True(card.Windows[0].HasPercent);
    }

    /// <summary>What a metered account is looked at for is how much money is left, and that is the whole
    /// of the line under its windows.</summary>
    [Fact]
    public void A_money_card_says_what_is_left_and_nothing_beside_it()
    {
        var report = new AiUsageReport("openrouter:abc", "OpenRouter", null,
            [new AiUsageWindow("today", TimeSpan.FromDays(1), UsedAmount: 4m)],
            RemainingCredit: 20.6m, Currency: "$", Now, Problem: null);

        var card = new UsageAccountViewModel(report, Now);

        // The separator is the reader's own, so the expectation is written the same way the card is:
        // what is pinned is that the figure and the word are there, not which locale drew them.
        Assert.True(card.HasMoney);
        Assert.Equal($"${20.60m:0.00}", card.RemainingLabel);

        // And it is the last thing on the account's own line rather than something docked to the end of
        // it, which is what lets it wrap with the windows when the card has to stack.
        var balance = Assert.Single(card.LineItems.Except(card.Windows));
        Assert.Equal("left:", balance.LabelWithColon);
        Assert.False(balance.HasPercent);
        Assert.Equal(card.RemainingLabel, balance.Figure);
    }

    /// <summary>What each window cost is on the window's own row, so the seven days the goal asks about
    /// are on screen and not summarised away into one figure.</summary>
    [Fact]
    public void Every_money_window_carries_its_own_amount()
    {
        var report = new AiUsageReport("openrouter:abc", "OpenRouter", null,
            [
                new AiUsageWindow("today", TimeSpan.FromDays(1), UsedAmount: 1.5m),
                new AiUsageWindow("7d", TimeSpan.FromDays(7), UsedAmount: 9m),
                new AiUsageWindow("30d", TimeSpan.FromDays(30), UsedAmount: 30m),
            ],
            RemainingCredit: 20.6m, Currency: "$", Now, Problem: null);

        var card = new UsageAccountViewModel(report, Now);

        Assert.All(card.Windows, window => Assert.True(window.HasAmount));
        Assert.Contains($"{9m:0.00}", card.Windows[1].AmountLabel);
        Assert.Contains($"{30m:0.00}", card.Windows[2].AmountLabel);
        // A money window has no percentage, so the row must not be relying on the bar to say anything.
        Assert.All(card.Windows, window => Assert.False(window.HasPercent));
    }

    /// <summary>A reading older than the window it describes is stamped, and one that is current is
    /// not: stamping every card would bury the one stamp that matters.</summary>
    [Fact]
    public void Only_a_reading_older_than_its_own_window_is_stamped()
    {
        Assert.False(new UsageAccountViewModel(Subscription("codex", 12), Now).IsStale);

        var old = Subscription("codex", 12) with { MeasuredAt = Now.AddHours(-6) };
        Assert.True(new UsageAccountViewModel(old, Now).IsStale);
    }

    /// <summary>
    /// "No account here reports limits." is a fact about the machine, so it waits until one was asked.
    /// </summary>
    /// <remarks>It was the first thing every usage tile said, for as long as the round took — and
    /// Claude Code alone allows fifteen seconds. Nothing asked is not nothing found.</remarks>
    [Fact]
    public async Task TheEmptyMessageWaitsForTheFirstAnswer()
    {
        var service = ServiceOf((AiUsageReport?)null);

        // Built before anything has answered, which is every usage tile for as long as the round takes.
        var waiting = new UsageTileViewModel(service);
        Assert.False(waiting.IsEmpty);
        waiting.Dispose();

        await service.RefreshAsync(force: true);

        var answered = new UsageTileViewModel(service);
        Assert.True(answered.IsEmpty);
        answered.Dispose();

        service.Dispose();
    }

    /// <summary>
    /// A forced refresh asks again; it does not hand back the round that was already running.
    /// </summary>
    /// <remarks>Joining is what made the button look broken: a round that began before the user
    /// finished signing in answers a different question, and its result reads on screen as a press that
    /// did nothing.</remarks>
    [Fact]
    public async Task AForcedRefreshDoesNotJoinTheRoundInFlight()
    {
        var gate = new TaskCompletionSource();
        var source = new SlowSource(gate.Task);
        var now = Now;
        var service = new AiUsageService(new SettingsService(), History(), _ => [source], () => now);

        var first = service.RefreshAsync();
        var forced = service.RefreshAsync(force: true);

        Assert.NotSame(first, forced);

        // The queued round only asks again once the source is actually stale — the round-in-flight
        // behaviour under test and the per-source throttle are two different things, so the clock is
        // moved past the window to isolate the first from the second.
        now += AiUsageService.RefreshInterval + TimeSpan.FromSeconds(1);
        gate.SetResult();
        await forced;

        Assert.Equal(2, source.Asked);

        service.Dispose();
    }

    /// <summary>
    /// One login read twice is one card, and the row the user named is the one that stays.
    /// </summary>
    /// <remarks>A machine that exports <c>CLAUDE_CONFIG_DIR</c> — which is what an mTiles sign-in sets
    /// for the tiles it launches — has its default account inside that sign-in's own directory, so the
    /// two are read from one file and drew the same figures twice under two names.</remarks>
    [Fact]
    public async Task One_login_reached_two_ways_gets_one_card()
    {
        const string sameFile = @"c:\accounts\max\.credentials.json";

        var service = ServiceOf(
            Subscription("claude:max", 14) with { SourceName = "Claude Code · Max", AccountKey = sameFile },
            Subscription("claude", 14) with { SourceName = "Claude Code", AccountKey = sameFile },
            Subscription("claude:pro", 4) with { SourceName = "Claude Code · Pro", AccountKey = "elsewhere" });

        await service.RefreshAsync(force: true);

        var tile = new UsageTileViewModel(service);
        Assert.Equal(["Claude Code · Max", "Claude Code · Pro"], tile.Accounts.Select(card => card.Title));

        tile.Dispose();
        service.Dispose();
    }

    /// <summary>A source that cannot say what account it is reading is never folded into another.</summary>
    /// <remarks>Two accounts wrongly merged is a subscription missing from the screen, which is worse
    /// than the repetition the merging exists to remove.</remarks>
    [Fact]
    public async Task Accounts_that_name_no_login_are_all_kept()
    {
        var service = ServiceOf(
            Subscription("one", 10) with { SourceName = "One" },
            Subscription("two", 20) with { SourceName = "Two" });

        await service.RefreshAsync(force: true);

        var tile = new UsageTileViewModel(service);
        Assert.Equal(2, tile.Accounts.Count);

        tile.Dispose();
        service.Dispose();
    }

    private static UsageHistory History() =>
        new(Path.Combine(Path.GetTempPath(), $"mtiles-usage-{Guid.NewGuid():N}", "history.json"));

    /// <summary>A source that answers when it is let go, and counts how often it was asked.</summary>
    private sealed class SlowSource(Task gate) : IUsageSource
    {
        private int _asked;

        public string Id { get; } = Guid.NewGuid().ToString();
        public int Asked => Volatile.Read(ref _asked);

        public async Task<AiUsageReport?> ReadAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _asked);
            await gate;
            return null;
        }
    }
}
