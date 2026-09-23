using mTiles.Models;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The word and tone the Goal tile's strip shows, in the Agent tile's vocabulary of colour.
/// </summary>
/// <remarks>
/// Each row is one of <see cref="GoalStatus"/>'s stated opinions: a pause outranks a run, a run with no
/// stage label still says it is working, a goal that stopped unmet wears Failed's colour, and a stop
/// reason says nothing until the run has reached its summary.
/// </remarks>
public class GoalStatusTests
{
    [Theory]
    [InlineData(false, false, false, GoalPhase.Goal, null, "", "Ready", AgentStatusTone.Ready)]
    [InlineData(true, false, false, GoalPhase.Implement, null, "Implement 2/5", "Implement 2/5", AgentStatusTone.Working)]
    [InlineData(true, false, false, GoalPhase.Plan, null, "", "Working", AgentStatusTone.Working)]
    [InlineData(true, true, false, GoalPhase.Implement, null, "Paused. Click Resume to continue.", "Paused", AgentStatusTone.Waiting)]
    [InlineData(false, true, true, GoalPhase.Clarify, null, "", "Paused", AgentStatusTone.Waiting)]
    [InlineData(true, false, true, GoalPhase.Plan, null, "Plan", "Plan", AgentStatusTone.Working)]
    [InlineData(false, false, true, GoalPhase.Clarify, null, "", "Waiting for you", AgentStatusTone.Waiting)]
    [InlineData(false, false, false, GoalPhase.Summary, GoalStopReason.Met, "", "Done", AgentStatusTone.Ready)]
    [InlineData(false, false, false, GoalPhase.Summary, GoalStopReason.Reviewed, "", "Done", AgentStatusTone.Ready)]
    [InlineData(false, false, false, GoalPhase.Summary, GoalStopReason.BudgetSpent, "", "Not met", AgentStatusTone.Failed)]
    [InlineData(false, false, false, GoalPhase.Summary, GoalStopReason.NoProgress, "", "Not met", AgentStatusTone.Failed)]
    [InlineData(false, false, false, GoalPhase.Summary, GoalStopReason.NoChange, "", "Not met", AgentStatusTone.Failed)]
    [InlineData(false, false, false, GoalPhase.Summary, null, "", "Ready", AgentStatusTone.Ready)]
    [InlineData(false, false, false, GoalPhase.Review, GoalStopReason.BudgetSpent, "", "Ready", AgentStatusTone.Ready)]
    public void The_status_speaks_the_agent_tiles_language(bool running, bool paused, bool waiting,
        GoalPhase phase, GoalStopReason? stop, string phaseLabel, string text, AgentStatusTone tone) =>
        Assert.Equal((text, tone), GoalStatus.Of(running, paused, waiting, phase, stop, phaseLabel));
}
