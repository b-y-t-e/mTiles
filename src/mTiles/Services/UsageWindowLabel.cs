namespace mTiles.Services;

/// <summary>
/// What a limit window is called on a card, from how long it is.
/// </summary>
/// <remarks>
/// <para>Derived rather than taken from the service's own word for it, because the words are not
/// comparable: Anthropic names its windows <c>five_hour</c> and <c>seven_day</c> while codex names the
/// same two lengths <c>primary</c> and <c>secondary</c> — and <c>primary</c> on a card next to
/// <c>5h</c> tells the reader nothing about which is which. The length is the fact both services do
/// state.</para>
/// <para>Pure, so the awkward cases — a length nobody stated, a window that is neither hours nor
/// days — are settled in a table rather than by finding a service that has one.</para>
/// </remarks>
public static class UsageWindowLabel
{
    /// <summary>What to call a window of this length.</summary>
    /// <remarks>Empty for a length that was not stated, which is the honest label: a window whose
    /// duration is unknown has no name, and calling it <c>0h</c> would put a figure on the card that
    /// nobody said.</remarks>
    public static string For(TimeSpan length) => length switch
    {
        { Ticks: <= 0 } => "",
        { TotalDays: >= 1 } when length.TotalDays % 1 == 0 => $"{(int)length.TotalDays}d",
        { TotalHours: >= 1 } when length.TotalHours % 1 == 0 => $"{(int)length.TotalHours}h",
        { TotalDays: >= 1 } => $"{length.TotalDays:0.#}d",
        _ => $"{length.TotalHours:0.#}h",
    };
}
