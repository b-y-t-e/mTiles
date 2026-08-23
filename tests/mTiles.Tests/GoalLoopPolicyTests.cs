using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The two rules the implement/review loop runs on. Both were inline conditions once, and both were
/// wrong in a way no test could reach: the loop needs an AI process and a git worktree to turn over
/// even once.
/// </summary>
public class GoalLoopPolicyTests
{
    // The verdict travels as its name rather than as itself: GoalLoopPolicy is internal, an xUnit test
    // class has to be public, and a public method may not take a parameter less accessible than itself.
    // nameof keeps the compiler checking that these are real cases.
    [Theory]
    [InlineData("a real answer", false, nameof(GoalRunVerdict.Answered))]
    [InlineData(null, false, nameof(GoalRunVerdict.Empty))]
    [InlineData("", false, nameof(GoalRunVerdict.Empty))]
    [InlineData("   \n  ", false, nameof(GoalRunVerdict.Empty))]
    // Cancelled wins over everything, including over an answer that arrived anyway: what matters is
    // that the user stopped the run, not how far it got.
    [InlineData(null, true, nameof(GoalRunVerdict.Cancelled))]
    [InlineData("a partial answer", true, nameof(GoalRunVerdict.Cancelled))]
    public void A_cancelled_run_is_never_an_empty_one(string? response, bool cancelled, string expected)
    {
        // The whole point of the distinction: Empty summarises the run, which moves the tile into a
        // phase Resume has no case for. Doing that to a pause made pausing a one-way door.
        Assert.Equal(expected, GoalLoopPolicy.Judge(response, cancelled).ToString());
    }

    [Fact]
    public void A_tool_that_crashed_is_not_a_tool_that_answered_nothing()
    {
        // The same trap NoTool was in: a process that would not start, or died halfway, may well work
        // on the next click, and ending the goal over it threw away an approved plan.
        Assert.Equal(GoalRunVerdict.Failed, GoalLoopPolicy.Judge(null, cancelled: false, failed: true));
    }

    [Fact]
    public void Stopping_a_run_is_what_it_is_even_though_killing_a_process_makes_it_throw()
    {
        // Cancellation is asked about before failure on purpose: killing a process is a normal way to
        // make it throw, and what the user meant is the more important of the two facts.
        Assert.Equal(GoalRunVerdict.Cancelled, GoalLoopPolicy.Judge(null, cancelled: true, failed: true));
    }

    [Fact]
    public void No_tool_at_all_outranks_a_failure_that_could_not_have_happened()
    {
        Assert.Equal(GoalRunVerdict.NoTool,
            GoalLoopPolicy.Judge(null, cancelled: true, toolMissing: true, failed: true));
    }

    [Fact]
    public void An_attempt_count_from_a_file_that_says_anything_is_clamped()
    {
        // `spent` comes off disk. Unclamped, a count above the budget was resumed at its own number and
        // the tile reported "attempt 99 of 5".
        Assert.Equal(5, GoalLoopPolicy.NextAttempt(spent: 99, max: 5, finishInterrupted: true));
        Assert.Null(GoalLoopPolicy.NextAttempt(spent: 99, max: 5, finishInterrupted: false));
    }

    [Theory]
    [InlineData(0, 5, 1)]
    [InlineData(1, 5, 2)]
    [InlineData(4, 5, 5)]
    public void A_fresh_lap_opens_the_next_attempt(int spent, int max, int expected)
    {
        Assert.Equal(expected, GoalLoopPolicy.NextAttempt(spent, max, finishInterrupted: false));
    }

    [Fact]
    public void The_budget_is_five_attempts_at_the_goal_and_that_is_where_it_ends()
    {
        Assert.Null(GoalLoopPolicy.NextAttempt(spent: 5, max: 5, finishInterrupted: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Resuming_finishes_the_attempt_already_paid_for(int spent)
    {
        // Not spent + 1. Charging twice for one attempt meant a run stopped and continued a few times
        // gave up while the user still had attempts left.
        Assert.Equal(spent, GoalLoopPolicy.NextAttempt(spent, max: 5, finishInterrupted: true));
    }

    [Fact]
    public void An_attempt_interrupted_as_the_last_of_the_budget_is_still_finishable()
    {
        // The budget question is asked after the resume question, not before it: `spent < max` alone
        // would refuse to reopen the loop and the fifth attempt would be lost half-done.
        Assert.Equal(5, GoalLoopPolicy.NextAttempt(spent: 5, max: 5, finishInterrupted: true));
    }

    [Fact]
    public void Resuming_a_run_that_never_started_one_starts_the_first()
    {
        // Reachable only from a state written before the first attempt was recorded. Opening attempt 1
        // is the answer that loses nothing; returning 0 would run an attempt the label counts as "0/5".
        Assert.Equal(1, GoalLoopPolicy.NextAttempt(spent: 0, max: 5, finishInterrupted: true));
    }
}
