using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// One row of a Goal tile's "planned by" or "reviewed by" chooser.
/// </summary>
/// <remarks>
/// <para>Its own type rather than a nullable <c>GoalAgentChoice</c>, because each list has one row that
/// is not an agent at all: "the agent doing the work" is the default answer and has to be readable as
/// such. A null in a bound list draws an empty line, which reads as a broken entry rather than as a
/// choice.</para>
/// <para>Named for what it is — a slot that may defer to the execution agent — rather than for the one
/// chooser it was first written for: planning and review ask the identical question, and a type called
/// <c>GoalAgentSlotChoice</c> filling the planning list would be a trap for the next reader.</para>
/// </remarks>
/// <param name="InstanceId">The <c>AiAgentInstance.Id</c> this row selects, or empty for the execution
/// agent.</param>
/// <param name="Label">What the row says.</param>
public sealed record GoalAgentSlotChoice(string InstanceId, string Label)
{
    /// <summary>The default: whoever is carrying the goal out does this job too.</summary>
    public static GoalAgentSlotChoice SameAsExecution { get; } = new("", "Same as execution");

    /// <summary>The row a configured agent makes.</summary>
    public static GoalAgentSlotChoice For(GoalAgentChoice choice) => new(choice.InstanceId, choice.Label);
}
