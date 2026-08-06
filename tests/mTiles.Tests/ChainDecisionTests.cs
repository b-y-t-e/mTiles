using mTiles.Services;
using Xunit;
using Step = mTiles.Services.ChainStep;

namespace mTiles.Tests;

/// <summary>
/// The launch chain's rules, as a table. They used to be reachable only by driving a real control on a
/// dispatcher and sleeping through the thresholds, which is why several of them were wrong for months:
/// the cases nobody could write a test for were the cases nobody checked.
/// </summary>
public class ChainDecisionTests
{
    private static readonly ChainPolicy T =
        new(MinLifetimeForRelaunch: 10_000, Established: 120_000, Retry: 200, Relaunch: 500);

    // A table in a local, not `[InlineData]`, because `ChainStep` is internal: a public theory method
    // cannot take a parameter less accessible than itself, and the enum being internal is worth more
    // than one test being a Theory. Each case carries its own message so a failure still names itself.
    [Fact]
    public void The_verdict_is_the_exit_code_and_the_lifetime_together()
    {
        (int? Code, long Lived, Step Expected, string Why)[] cases =
        [
            // A clean exit after real use is the user closing the tool: bring it back.
            (0, 200_000, Step.RestartChain, "quit after a long session"),
            (0, 10_000, Step.RestartChain, "exactly at the bar counts as having run"),
            // Cleanly but at once: it did not stick, and the fallback is what a profile names for that.
            (0, 9_999, Step.NextCommand, "clean but just short of the bar"),
            (0, 0, Step.NextCommand, "clean and instant"),
            // A failure means "this command is no good" or "a working tool crashed" — the lifetime says
            // which. 21s is `claude -r <unknown-id>` reporting an invalid session: the failure that has
            // to land on the "did not work" side, and did not while the rule was about survival alone.
            (1, 21_000, Step.NextCommand, "the 21-second failure"),
            (1, 119_999, Step.NextCommand, "just short of established"),
            (1, 120_000, Step.Relaunch, "exactly established"),
            (1, 600_000, Step.Relaunch, "a working tool crashing"),
            // -1 is a code a child may genuinely return: a failure like any other non-zero one.
            (-1, 1_000, Step.NextCommand, "a quick -1"),
            (-1, 600_000, Step.Relaunch, "a late -1"),
            // No code at all — a lost connection. No way to reach it either, so: a failure.
            (null, 1_000, Step.NextCommand, "a quick disconnect"),
            (null, 600_000, Step.Relaunch, "a late disconnect"),
        ];

        foreach (var (code, lived, expected, why) in cases)
            Assert.True(T.Decide(code, lived) == expected,
                $"{why} (exit {code?.ToString() ?? "none"} after {lived} ms): "
                + $"expected {expected}, got {T.Decide(code, lived)}");
    }

    /// <summary>Neither input decides alone, and this is the shape of that: the same code means opposite
    /// things at different lifetimes, and the same lifetime means opposite things for different codes.
    /// Every bug this chain has had was an attempt to read one of the two on its own.</summary>
    [Fact]
    public void Neither_the_code_nor_the_lifetime_decides_on_its_own()
    {
        Assert.NotEqual(T.Decide(1, 1_000), T.Decide(1, 600_000));
        Assert.NotEqual(T.Decide(0, 600_000), T.Decide(1, 600_000));
    }

    /// <summary>
    /// Quitting a tool you have been working in is not a malfunction, and doing it four times in a
    /// morning is not a loop. Charging those to the budget would make the tile answer the fourth quit
    /// by refusing to bring the tool back — punishing the one case the feature exists to serve.
    /// </summary>
    [Theory]
    [InlineData(0, 600_000, false)]     // a real session, closed on purpose: free
    [InlineData(0, 120_000, false)]     // exactly established, still free
    [InlineData(0, 119_999, true)]      // a "clean" exit this quick is indistinguishable from a loop
    [InlineData(0, 11_000, true)]
    [InlineData(1, 600_000, true)]      // crashes are charged however long they took to arrive
    [InlineData(null, 600_000, true)]
    public void Only_a_deliberate_quit_after_real_use_is_free(int? exitCode, long lived, bool charged)
        => Assert.Equal(charged, T.CountsAgainstBudget(exitCode, lived));

    /// <summary>
    /// Thresholds that cannot mean what they say are refused up front. <c>Established</c> below
    /// <c>MinLifetimeForRelaunch</c> is the one that matters: there would be a band of lifetimes in
    /// which a failure counts as a working tool crashing while a clean exit still counts as never
    /// having got going — the chain relaunching what fails and giving up on what succeeds.
    /// </summary>
    [Fact]
    public void Thresholds_that_contradict_each_other_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainPolicy(MinLifetimeForRelaunch: 10_000, Established: 5_000, Retry: 1, Relaunch: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainPolicy(MinLifetimeForRelaunch: -1, Established: 1, Retry: 1, Relaunch: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChainPolicy(10, 20, 1, 1, MaxRelaunches: 3, RelaunchWindow: 0).Validate());

        T.Validate();   // and the real ones are coherent
    }
}
