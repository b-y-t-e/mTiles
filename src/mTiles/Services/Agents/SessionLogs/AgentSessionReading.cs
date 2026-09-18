namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// What an agent's own session store says about one conversation: which it is, and how much of the
/// model's context it has spent.
/// </summary>
/// <remarks>
/// <para><b>Every figure but the id is nullable, and null is <i>did not say</i> rather than zero</b> —
/// the rule <c>AiUsageWindow</c> already follows. Measured 2026-09-18 across the six CLIs, no two of
/// them say the same things: codex alone names the context window it is working in, agy says nothing at
/// all, and only pi, opencode and grok record what a turn cost in money. A missing figure read as zero
/// would draw a bar that never moves and a price of nothing on an account that has been spending all
/// afternoon.</para>
/// <para>One record for all six rather than a shape per agent, because what is done with it is the same
/// in every case — a session id to resume and a gauge to fill — and the differences are all in the
/// reading, which is what <see cref="IAgentSessionLog"/> is for.</para>
/// </remarks>
/// <param name="SessionId">The id this CLI would resume the conversation by. The one field that is
/// never absent: a reading that cannot name its session is not a reading.</param>
/// <param name="UpdatedAt">When the store last wrote about it, which is how the newest of several
/// sessions in one directory is chosen.</param>
/// <param name="UsedTokens">What is currently occupying the model's context — the input, the cache and
/// the output of the last turn, spelled whichever way that CLI spells them.</param>
/// <param name="ContextWindow">The window those tokens are being spent against, where the CLI names it.
/// Only codex does; for the rest this is null and the caller fills it in from
/// <c>ModelContextWindow</c>, which is the same answer arrived at from the provider's side.</param>
/// <param name="CostUsd">What the conversation has cost so far, where the CLI keeps a running total.</param>
/// <param name="Model">The model the conversation's last turn actually ran on, where the store records
/// it. <b>Not the instance's setting, and that is the point</b>: an agent on a subscription is usually
/// configured with no model at all — the CLI picks its own — and the user can change it mid-conversation
/// from inside the TUI. Asking the account how large that model's context is needs its id, and the
/// transcript is the only place that has it.</param>
public sealed record AgentSessionReading(
    string SessionId,
    DateTimeOffset UpdatedAt,
    long? UsedTokens = null,
    long? ContextWindow = null,
    decimal? CostUsd = null,
    string? Model = null);
