namespace mTiles.Models;

/// <summary>
/// One limit window an account reports — how long it lasts, how much of it is gone, and when it comes
/// back.
/// </summary>
/// <remarks>
/// <para><b>Every figure is nullable and <c>null</c> means <i>did not say</i></b>, for the reason
/// <c>AiModelInfo.ContextWindowTokens</c> and <c>ProviderCheck.Balance</c> already carry: a limit shown
/// as 0 tells a user whose account works perfectly well that they have run out.</para>
/// <para><b>A percentage and an amount are not two views of one number.</b> A subscription answers in
/// percent and a provider in money, so both live here and a card draws whichever it was given rather
/// than converting one into the other — there is no rate to convert with, and inventing one would put a
/// figure on screen nobody's service ever said.</para>
/// </remarks>
/// <param name="Label">What the window is called on screen — <c>5h</c>, <c>7d</c>.</param>
/// <param name="Length">How long the window is, which is what <c>UsagePace</c> measures elapsed time
/// against. <see cref="TimeSpan.Zero"/> for a window whose length the service does not state, and the
/// pace is then unknown rather than guessed.</param>
/// <param name="UsedPercent">How much of the window is spent, 0..100, or null where the service answers
/// in money instead.</param>
/// <param name="UsedAmount">What has been spent in this window, or null where the service answers in
/// percent instead.</param>
/// <param name="LimitAmount">What the window allows, where there is a stated ceiling. Null is an
/// unmetered key, which is not the same as a ceiling of zero.</param>
/// <param name="ResetsAt">When the window starts again, or null where the service names no instant.</param>
public sealed record AiUsageWindow(
    string Label,
    TimeSpan Length,
    double? UsedPercent = null,
    decimal? UsedAmount = null,
    decimal? LimitAmount = null,
    DateTimeOffset? ResetsAt = null);
