using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the waiting row calls each phase, and when it carries an attempt with it.
/// </summary>
public class GoalStageDisplayTests
{
    [Theory]
    [InlineData(GoalPhase.Goal, "working out the goal")]
    [InlineData(GoalPhase.Clarify, "clarifying")]
    [InlineData(GoalPhase.Plan, "planning")]
    [InlineData(GoalPhase.Summary, "summarising")]
    public void Names_the_phase_without_a_number_where_there_is_only_ever_one_lap(
        GoalPhase phase, string expected)
        => Assert.Equal(expected, GoalStageDisplay.Short(phase, attempt: 1, attempts: 5));

    [Theory]
    [InlineData(GoalPhase.Implement, "implementing · 2/5")]
    [InlineData(GoalPhase.Review, "reviewing · 2/5")]
    public void Carries_the_attempt_on_the_two_phases_that_repeat(GoalPhase phase, string expected)
        => Assert.Equal(expected, GoalStageDisplay.Short(phase, attempt: 2, attempts: 5));

    [Theory]
    // Before the loop has taken its first lap.
    [InlineData(0, 5)]
    // A budget lowered mid-run leaves the attempt outside it — "6/5" undermines every other number on
    // the tile, and the word on its own is still true.
    [InlineData(6, 5)]
    // Nothing to be a fraction of.
    [InlineData(1, 0)]
    public void Drops_a_fraction_that_would_not_make_sense(int attempt, int attempts)
        => Assert.Equal("implementing", GoalStageDisplay.Short(GoalPhase.Implement, attempt, attempts));
}
