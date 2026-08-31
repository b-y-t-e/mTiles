namespace mTiles.Models;

/// <summary>
/// One model a provider serves, as much of it as the provider is willing to say.
/// </summary>
public sealed record AiModelInfo
{
    /// <summary>What has to be passed to ask for this model — the id, never the label.</summary>
    public required string Id { get; init; }

    /// <summary>What to show, falling back to the id where the provider offers no better name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Whether this model is loaded and answering right now, where the provider says. Only a
    /// local server has an opinion; <c>null</c> everywhere else, and <c>null</c> is not <c>false</c>.
    /// </summary>
    public bool? IsLoaded { get; init; }

    /// <summary>
    /// The effort levels this model accepts, or <c>null</c> for "the provider did not say".
    /// </summary>
    /// <remarks>
    /// <para><b><c>null</c> is unknown and must never become "no effort available"</b> — the same
    /// tri-state rule the workspace panel's <c>HasRepository</c> follows, and for the same reason: in
    /// practice only OpenRouter answers this honestly (<c>supported_parameters</c> per model), so a
    /// missing answer is the common case rather than the exception.</para>
    /// <para>Whether effort can be passed <em>at all</em> is the agent's to say, not the model's:
    /// Claude Code's <c>--effort</c> is its own abstraction over a thinking budget and owes nothing to
    /// the provider's list. So this narrows the agent's list where it is known and is ignored where it
    /// is not — see <c>AiProviderCatalog.NarrowEfforts</c>.</para>
    /// </remarks>
    public IReadOnlyList<AiEffort>? SupportedEfforts { get; init; }

    /// <summary>
    /// The model's context window in tokens, or <c>null</c> for "the provider did not say".
    /// </summary>
    /// <remarks>
    /// <para>The same tri-state rule as <see cref="SupportedEfforts"/>: <c>null</c> is unknown and must
    /// never become a number, because a number guessed here reaches an agent's environment as a fact.
    /// OpenRouter answers honestly (<c>context_length</c> per model), LM Studio carries
    /// <c>max_context_length</c> on its own listing, and Ollama only on a per-model
    /// <c>api/show</c> call — so the field is filled by whichever of those the caller asked for, and
    /// stays null where the provider said nothing.</para>
    /// <para>Read by <c>ModelContextWindow</c>, which turns it into Claude Code's
    /// <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> for an instance on a third-party provider — the case
    /// where the CLI does not know the model and assumes a window that can be wrong by half.</para>
    /// </remarks>
    public long? ContextWindowTokens { get; init; }
}
