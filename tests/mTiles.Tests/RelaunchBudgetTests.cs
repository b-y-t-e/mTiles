using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The bound on relaunching. It exists because every rule above is right in the small and loops in the
/// large: something that qualifies, ends, and qualifies again is a rule being obeyed forever.
/// </summary>
public class RelaunchBudgetTests
{
    [Fact]
    public void A_budget_allows_its_quota_and_then_stops()
    {
        var budget = new RelaunchBudget(max: 3, window: 60_000);

        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());
        Assert.False(budget.TrySpend());     // and stays stopped
    }

    /// <summary>Zero is a coherent setting, not a degenerate one: a profile whose command must never be
    /// brought back automatically. It has to mean "never", not "once".</summary>
    [Fact]
    public void A_budget_of_zero_never_allows_anything()
        => Assert.False(new RelaunchBudget(max: 0, window: 60_000).TrySpend());

    /// <summary>A rate, not a running total — the difference between "this is looping" and "this has
    /// been used for weeks". A total would give up on a tool the user works in daily once its fourth
    /// crash came round, however many months apart the four were.</summary>
    [Fact]
    public void A_budget_that_outlives_its_window_starts_over()
    {
        // Time is moved, not waited out: a test that sleeps through a real window either takes as long
        // as the window or shrinks it until what is really being measured is the scheduler.
        long now = 0;
        var budget = new RelaunchBudget(max: 2, window: 1000, now: () => now);

        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());

        now += 1001;
        Assert.True(budget.TrySpend());
    }

    /// <summary>Sliding, not tumbling. With a counter reset every window, a burst straddling the
    /// boundary spends the last of one window and the first of the next back to back — twice the limit
    /// at exactly the moment something is going wrong fast.</summary>
    [Fact]
    public void Spending_expires_one_at_a_time_rather_than_all_at_once()
    {
        long now = 0;
        var budget = new RelaunchBudget(max: 2, window: 1000, now: () => now);

        Assert.True(budget.TrySpend());      // at 0
        now += 600;
        Assert.True(budget.TrySpend());      // at 600
        Assert.False(budget.TrySpend());

        now += 500;                          // 1100: the first has expired, the second has not
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());
    }
}
