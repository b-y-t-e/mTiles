namespace mTiles.Models;

/// <summary>
/// What one account answered when it was asked how much of its allowance is left.
/// </summary>
/// <remarks>
/// <para><see cref="Problem"/> is a sentence, because "could not be asked" and "has spent nothing"
/// are different facts and a zero says the second about the first. <b>Where the sentence goes is the
/// reader's decision, not this type's</b>: the usage tile draws no card for such an account and
/// <c>AiUsageService.Explain</c> puts the reason in the log, because most of these are an account the
/// user does not reach through this machine and a dashboard whose top line is permanently a complaint
/// about one of them is a dashboard they stop reading. What this type guarantees is that the two are
/// distinguishable at all.</para>
/// <para><see cref="MeasuredAt"/> is on the report rather than on the service, because the accounts are
/// not all as fresh as each other: codex's figures are as old as its last reply, and a reading older
/// than the window it describes has to be stamped rather than shown as current.</para>
/// </remarks>
/// <param name="SourceId">Stable identity of the account this describes, and the key its daily
/// snapshots are filed under. Derived from ids that are already stable — an agent's and a sign-in's, or
/// a provider instance's — never from a name the user can retype.</param>
/// <param name="SourceName">What the card is titled.</param>
/// <param name="Plan">The subscription behind it where the service names one, else null.</param>
/// <param name="Windows">The limit windows, in the order they should be drawn. Empty for an account
/// that reports none, which is a card of money and no bars rather than an error.</param>
/// <param name="RemainingCredit">What is left to spend, where the answer is money. Null is "did not
/// say" — an unmetered key has no remaining figure at all.</param>
/// <param name="Currency">The symbol <see cref="RemainingCredit"/> and the amounts are in, or null
/// where the service names none.</param>
/// <param name="MeasuredAt">The instant the figures describe — <b>not</b> the instant they were
/// fetched, where the two differ.</param>
/// <param name="Problem">Why this account has no figures, or null when it answered.</param>
public sealed record AiUsageReport(
    string SourceId,
    string SourceName,
    string? Plan,
    IReadOnlyList<AiUsageWindow> Windows,
    decimal? RemainingCredit,
    string? Currency,
    DateTimeOffset MeasuredAt,
    string? Problem)
{
    /// <summary>An account that is there and could not be asked.</summary>
    /// <remarks>A report rather than a null, and the difference is what the caller can then do about
    /// it: null is an account this machine does not have and nothing is recorded, while this is one it
    /// has and could not ask, which is worth a line in the log even where it is worth no card.</remarks>
    public static AiUsageReport Failed(string sourceId, string sourceName, string problem,
        DateTimeOffset measuredAt) =>
        new(sourceId, sourceName, null, [], null, null, measuredAt, problem);

    /// <summary>Whether anything on this card can be drawn as a figure.</summary>
    public bool Answered => Problem is null;
}
