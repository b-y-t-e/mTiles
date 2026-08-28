using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// What a pasted image looks like in the text the user is writing.
/// </summary>
/// <remarks>
/// <para>One place that knows the spelling, because the same string is written into the composer and
/// then read back out of the goal — <c>GoalWorkflowEngine.StartNewGoal</c> asks which markers the new
/// goal still refers to. Two spellings of it would drop every image out of the goal that had just been
/// typed, silently, and the marker would go to the tool naming a file nothing had kept.</para>
/// <para>The shape is Claude Code's own, and that is the whole of the reasoning: this is what somebody
/// who pastes a screenshot at an agent already expects to see appear where their caret was.</para>
/// </remarks>
public static partial class GoalImageMarker
{
    public static string For(int index) => $"[Image #{index}]";

    /// <summary>
    /// The text with every marker removed whose image is not in <paramref name="keptIndexes"/>.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart of what <c>GoalWorkflowEngine.StartNewGoal</c> does to the list: it keeps
    /// only the images the new goal still refers to, so a marker left behind in the composer — pasted,
    /// and then the goal replaced by + or by one detected from the working tree — now stands for
    /// nothing. Sent as it is, it reaches the tool with no path beside it, which is the one thing the
    /// insertion rule in <c>GoalTileViewModel.AttachImage</c> refuses to do.</para>
    /// <para>The single space after the marker goes with it, because that is what was inserted with it;
    /// taking it as well is what stops a removal leaving a double space in the middle of a sentence.
    /// </para>
    /// </remarks>
    public static string DropMarkersExcept(string text, IReadOnlyCollection<int> keptIndexes) =>
        string.IsNullOrEmpty(text)
            ? text
            : MarkerPattern().Replace(text, match =>
                int.TryParse(match.Groups[1].Value, out var index) && keptIndexes.Contains(index)
                    ? match.Value
                    : "");

    [GeneratedRegex(@"\[Image #(\d+)\] ?")]
    private static partial Regex MarkerPattern();
}
