using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// One row of the Goal tile's "reviewed by" chooser.
/// </summary>
/// <remarks>
/// Its own type rather than a nullable <c>GoalAgentChoice</c>, because the list has one row that is not
/// an agent at all: "the agent doing the work" is the default answer and has to be readable as such. A
/// null in a bound list draws an empty line, which reads as a broken entry rather than as a choice.
/// </remarks>
/// <param name="InstanceId">The <c>AiAgentInstance.Id</c> this row selects, or empty for the execution
/// agent.</param>
/// <param name="Label">What the row says.</param>
public sealed record GoalReviewerChoice(string InstanceId, string Label)
{
    /// <summary>The default: whoever is carrying the goal out reviews it too.</summary>
    public static GoalReviewerChoice SameAsExecution { get; } = new("", "Same as execution");

    /// <summary>The row a configured agent makes.</summary>
    public static GoalReviewerChoice For(GoalAgentChoice choice) => new(choice.InstanceId, choice.Label);
}
