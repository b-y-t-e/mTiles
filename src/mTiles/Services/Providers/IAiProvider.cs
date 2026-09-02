using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// One service an agent can be pointed at — where it lives, what wire format it serves, and how to ask
/// it whether it is there.
/// </summary>
/// <remarks>
/// <para>One class per provider, keyed by a string id the way <c>IAiAgent</c> and <c>IShellTerminal</c>
/// are. What differs between them is small and entirely factual: the address, whether a key is needed,
/// which <see cref="ApiFlavor"/>s the endpoint speaks, and how the model list is spelled.</para>
/// <para><b>The flavors are the whole point of the type.</b> An agent and a provider are compatible
/// exactly when their lists intersect — see <c>AiProviderCatalog.IsCompatible</c> — which is what keeps
/// the UI from offering codex plus a local server: both are "OpenAI", and they do not work together,
/// because codex speaks <see cref="ApiFlavor.OpenAiResponses"/> and a local server serves
/// <see cref="ApiFlavor.OpenAiChatCompletions"/>.</para>
/// </remarks>
public interface IAiProvider
{
    /// <summary>Stable, lowercase, and what settings store — <c>"openrouter"</c>, <c>"ollama"</c>.
    /// Never shown to the user.</summary>
    string Id { get; }

    /// <summary>What the user is shown — <c>"OpenRouter"</c>, <c>"LM Studio"</c>.</summary>
    string DisplayName { get; }

    /// <summary>The wire formats this provider serves.</summary>
    IReadOnlyList<ApiFlavor> ApiFlavors { get; }

    /// <summary>Where it lives when the instance names no address of its own, or null for a provider
    /// that has no address until somebody gives it one — which is every local server.</summary>
    Uri? DefaultBaseUrl { get; }

    /// <summary>The port to fill in for an address typed without one. Only a local server is typed as a
    /// bare host, which is the only place this is read.</summary>
    int DefaultPort { get; }

    /// <summary>
    /// The environment variable this service's own key is conventionally read from, or null where it
    /// has no key at all.
    /// </summary>
    /// <remarks>
    /// <para><b>A fact about the service, not about any agent</b>, which is why it lives here:
    /// <c>OPENROUTER_API_KEY</c> is what OpenRouter's key is called everywhere it is read, and both
    /// opencode and pi document exactly these spellings. Put on the agents instead it would be the same
    /// table written out five times, and the fifth copy would be the one that drifted.</para>
    /// <para>Measured 2026-08-31: an agent handed <c>OPENAI_API_KEY</c> for an OpenRouter instance does
    /// not reach OpenRouter — <c>opencode auth list</c> reports it as the <b>OpenAI</b> provider, so the
    /// run authenticates against api.openai.com while every row on screen says OpenRouter.</para>
    /// <para>Null for the local servers, which have no authentication: their address is the whole of
    /// how they are reached, and that is <see cref="EndpointFor"/>'s business.</para>
    /// </remarks>
    string? KeyEnvironmentVariable { get; }

    /// <summary>
    /// What model catalogues call this service — the <c>provider</c> half of <c>provider/model</c>.
    /// </summary>
    /// <remarks><para><b>Not every agent takes a bare model id.</b> opencode's own help says
    /// <c>--model</c> is "model to use in the format of provider/model", and it refuses anything else
    /// with <c>ProviderModelNotFoundError</c> before a call is made; pi takes the same shape, or a
    /// separate <c>--provider</c>. The name they use is the service's, so it belongs to the service.
    /// </para>
    /// <para><b>Measured against models.dev</b>, which is the catalogue opencode reads, on 2026-08-31:
    /// <c>openrouter</c>, <c>anthropic</c>, <c>zai</c> and <c>openai</c> are spelled there exactly as
    /// they are spelled here, so every hosted provider an agent can be prefixed with is confirmed
    /// rather than assumed. The two local ones need no entry in anybody's catalogue — the provider is
    /// declared to the agent by <c>OpenCodeProviderConfig</c>, under this same id, so the document and
    /// the prefix agree by construction.</para></remarks>
    string CatalogueId { get; }

    /// <summary>
    /// Whether a key is needed to talk to it at all.
    /// </summary>
    /// <remarks>False for a local server, and that is worth saying out loud rather than treating as an
    /// empty field: <b>neither LM Studio nor Ollama has any authentication</b>, so a discovered
    /// instance is open to everyone on that network.</remarks>
    bool NeedsApiKey { get; }

    /// <summary>Whether it runs on this machine or this network. What decides whether discovery and the
    /// "first loaded model" sentinel apply, and whether "usually finds nothing" needs saying.</summary>
    bool IsLocal { get; }

    /// <summary>
    /// The credential a client of this provider authenticates with, or a word that stands in for one
    /// where the server takes none.
    /// </summary>
    /// <remarks>
    /// <para>Asked by the agents that present a token to an endpoint — Claude Code's
    /// <c>ANTHROPIC_AUTH_TOKEN</c> is the reader — so "what do I authenticate with here" is answered by
    /// the provider, which is the one that knows whether its server takes a key and, when the server
    /// manages its own, which one.</para>
    /// <para><b>An empty string is an answer, and a deliberate one.</b> Measured 2026-08-31 against
    /// Claude Code 2.1.251, <c>ANTHROPIC_AUTH_TOKEN=""</c> fails with "Not logged in · Please run
    /// /login" before a request is made — which is the right diagnosis for a hosted provider whose key
    /// was never typed (the form allows saving one, since only the name is required): the lack is local
    /// and named here, where a placeholder word would buy a 401 from the provider's own server that
    /// reads as a rejected key. A server that takes no key at all gets the word instead — any
    /// non-empty value passes it (measured against LM Studio the same day), and the word is chosen to
    /// be nothing that could be mistaken for a credential.</para>
    /// </remarks>
    string ClientToken(AiProviderInstance instance);

    /// <summary>
    /// What the form and the row say where no key is typed, in one sentence.
    /// </summary>
    /// <remarks>One spelling in both places, because two sentences about the same fact are two places
    /// for them to drift. The default is a fact about the network; an override is for a server that
    /// takes a credential but hands it out itself.</remarks>
    string NoKeyNote { get; }

    /// <summary>
    /// The address to hand an agent that speaks <paramref name="flavor"/>, or null when this provider
    /// does not serve it.
    /// </summary>
    /// <remarks>Per flavor and not one address per provider, because two of them serve two shapes at
    /// two paths: z.ai's Anthropic-compatible endpoint is <c>/api/anthropic</c> while its OpenAI one is
    /// elsewhere, and an agent given the wrong one of those fails with somebody else's 404. The
    /// alternative — every agent knowing every provider's path — is the map this whole layer exists to
    /// avoid.</remarks>
    Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance);

    /// <summary>
    /// Where this instance's calls go — its own address, or this provider's published one.
    /// </summary>
    /// <remarks>Null means the instance names an address that cannot be read, which is deliberately
    /// not the same answer as "none was typed": that one falls back. <c>AgentAvailability</c> reads it
    /// so a typo is refused by a sentence rather than by a launch that silently runs on the CLI's own
    /// configuration.</remarks>
    Uri? BaseUrlFor(AiProviderInstance instance);

    /// <summary>Is it reachable, and does this key work? Never throws: a failure is the answer.</summary>
    Task<ProviderCheck> TestAsync(AiProviderInstance instance, CancellationToken ct = default);

    /// <summary>
    /// The models it serves, or an empty list when it cannot be asked.
    /// </summary>
    /// <remarks>The list can be long — <c>opencode models</c> returned 374 entries against OpenRouter —
    /// so what shows it is a searchable field and not a combo box.</remarks>
    Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default);

    /// <summary>
    /// One model's context window in tokens, or null when neither the listing nor the provider says.
    /// </summary>
    /// <remarks><para>Asked for one model rather than read off <see cref="ModelsAsync"/> because two of
    /// the providers answer per model: Ollama's listing carries no window at all, only its
    /// <c>api/show</c> does. The default asks the list; the override asks the model.</para>
    /// <para><b>Null is "did not say"</b> and is answered, not worked around — a guessed window reaches
    /// an agent's environment as a fact.</para></remarks>
    Task<long?> ContextWindowAsync(AiProviderInstance instance, string model,
        CancellationToken ct = default);

    /// <summary>
    /// What is left on this key and what the last windows cost, or null when the service reports
    /// nothing of the kind.
    /// </summary>
    /// <remarks><para>Null is "there is no such question here" — the local servers meter nothing and
    /// the hosted ones but one publish no spending endpoint — and nothing is recorded at all. A report
    /// carrying a <c>Problem</c> is a key that exists and could not be asked, and its sentence reaches
    /// the log through <c>AiUsageService.Explain</c>. Neither draws a card. Neither is ever a zero
    /// either: an unmetered key and an exhausted one must not read alike, the same tri-state
    /// <see cref="ProviderCheck.Balance"/> already carries.</para>
    /// <para>Never throws, for the reason <see cref="TestAsync"/> does not.</para></remarks>
    Task<AiUsageReport?> UsageAsync(AiProviderInstance instance, CancellationToken ct = default);
}
