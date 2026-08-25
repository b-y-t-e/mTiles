using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>
/// One severity's count, as the status strip shows it.
/// <para>A collection rather than four view-model properties and four blocks of markup, each of which
/// could be forgotten on its own. What a fifth severity costs now, honestly counted: an enum member, a
/// letter below, a label in <c>GoalTranscript</c>, a synonym list in <c>GoalResponseParser</c>, a
/// sentence in the review prompt, one class binding and one style — and, if it is to block anything, a
/// rule in <c>GoalCompletionPolicy</c>. Seven places, not one.</para>
/// <para>Most of those are decisions rather than mechanics: what the level <em>means</em>, what a tool
/// should call it, and what it stops. No indirection supplies those, so this does not make
/// <see cref="GoalSeverity"/> open — it removes the duplication that was pure bookkeeping and leaves
/// the part that needs a person.</para>
/// </summary>
public sealed class GoalBadge
{
    public required GoalSeverity Severity { get; init; }
    public required int Count { get; init; }

    /// <summary>
    /// The count and the severity's letter: <c>2E</c>, <c>1B</c>.
    /// <para>Spelled out rather than taken from the first character, which is a coincidence and not a
    /// rule: a fifth severity beginning with S would silently share a badge with Suggestion, and the
    /// only sign would be two identical letters in the strip. A switch makes the clash a decision
    /// somebody has to take rather than one they can trip over.</para>
    /// </summary>
    public string Text => $"{Count}{Letter}";

    private char Letter => Severity switch
    {
        GoalSeverity.Blocker => 'B',
        GoalSeverity.Error => 'E',
        GoalSeverity.Warning => 'W',
        GoalSeverity.Suggestion => 'S',
        _ => '?',
    };

    public string Tooltip =>
        $"{Count} {Severity.ToString().ToLowerInvariant()}{(Count == 1 ? "" : "s")} in the last review";

    // Bound as style classes rather than as brushes, so a theme change repaints them — the rule the
    // phase dot follows and for the same reason.
    public bool IsBlocker => Severity == GoalSeverity.Blocker;
    public bool IsError => Severity == GoalSeverity.Error;
    public bool IsWarning => Severity == GoalSeverity.Warning;
    public bool IsSuggestion => Severity == GoalSeverity.Suggestion;
}
