using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Which stage of the goal a run is in, in the two or three words the waiting row has space for.
/// </summary>
/// <remarks>
/// <para>Not the same sentence as the status strip, and deliberately so. The strip says <c>AI is
/// implementing (attempt 2/5)...</c> — a sentence, because it is the tile's one line of status and has
/// a bar to itself. This sits at the end of a row of dots inside the conversation, where a sentence
/// would read as a message the tool had written; it is a label on the wait, so it is written as one.
/// </para>
/// <para>The attempt is carried on the two phases that repeat, and nowhere else. On <c>clarifying</c> or
/// <c>planning</c> a fraction would be the same <c>1/5</c> every time — a number that never moves is one
/// the reader learns to stop looking at, which is the last thing it should teach them before the loop
/// where it does move.</para>
/// </remarks>
internal static class GoalStageDisplay
{
    /// <summary>The stage, as the waiting row writes it.</summary>
    /// <param name="phase">The phase the workflow is in.</param>
    /// <param name="attempt">The implement/review lap being run, 1-based.</param>
    /// <param name="attempts">How many laps are allowed.</param>
    public static string Short(GoalPhase phase, int attempt, int attempts) => phase switch
    {
        // The run in this phase is Detect: the tool is reading the working tree to say what the goal
        // is. "working out the goal" rather than "detecting", which describes the button, not the wait.
        GoalPhase.Goal => "working out the goal",
        GoalPhase.Clarify => "clarifying",
        GoalPhase.Plan => "planning",
        GoalPhase.Implement => WithAttempt("implementing", attempt, attempts),
        GoalPhase.Review => WithAttempt("reviewing", attempt, attempts),
        GoalPhase.Summary => "summarising",
        _ => "working",
    };

    /// <remarks>
    /// A fraction is only written when both numbers make one — an attempt inside a budget with room for
    /// it. <c>0/0</c> before the loop has started, or <c>6/5</c> from a budget lowered mid-run, is a
    /// number that undermines every other number on the tile; the word alone is still true.
    /// </remarks>
    private static string WithAttempt(string stage, int attempt, int attempts) =>
        attempt >= 1 && attempt <= attempts ? $"{stage} · {attempt}/{attempts}" : stage;
}
