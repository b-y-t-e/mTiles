namespace mTiles.Services;

/// <summary>
/// What a pasted image looks like in the text the user is writing.
/// </summary>
/// <remarks>
/// <para>One place that knows the spelling, because the same string is written into the composer and
/// then read back out of the goal — <c>GoalWorkflowEngine.StartNewGoal</c> asks which markers the new
/// goal still refers to. Two spellings of it would drop every image out of the goal that had just been
/// typed, silently, and the marker would go to the tool naming a file nothing had kept.</para>
/// <para>The spelling itself is <see cref="mTiles.AgentSessions.ImageMarkers"/>'s, which the Agent tile's
/// composer and every session read too — so a marker means the same thing in both tiles.</para>
/// </remarks>
public static class GoalImageMarker
{
    public static string For(int index) => mTiles.AgentSessions.ImageMarkers.For(index);

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
        mTiles.AgentSessions.ImageMarkers.DropExcept(text, keptIndexes);
}
