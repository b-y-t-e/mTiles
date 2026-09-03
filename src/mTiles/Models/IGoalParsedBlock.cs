namespace mTiles.Models;

/// <summary>
/// The two things every phase's answer has in common: whether the JSON block this tile asked for was
/// read, and the text it was read from.
/// </summary>
/// <remarks>
/// It exists for the one round that treats a clarification and a review identically — the re-send of a
/// block that arrived broken. That round asks nothing about questions or findings, so it depends on
/// nothing else; adding a phase to it is implementing these two members rather than a third copy of
/// the same method.
/// </remarks>
public interface IGoalParsedBlock
{
    /// <inheritdoc cref="GoalReviewResult.WasStructured"/>
    bool WasStructured { get; }

    /// <summary>The answer as the tool wrote it.</summary>
    string RawText { get; }
}
