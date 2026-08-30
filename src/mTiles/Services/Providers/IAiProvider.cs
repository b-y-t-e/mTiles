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
    /// The address to hand an agent that speaks <paramref name="flavor"/>, or null when this provider
    /// does not serve it.
    /// </summary>
    /// <remarks>Per flavor and not one address per provider, because two of them serve two shapes at
    /// two paths: z.ai's Anthropic-compatible endpoint is <c>/api/anthropic</c> while its OpenAI one is
    /// elsewhere, and an agent given the wrong one of those fails with somebody else's 404. The
    /// alternative — every agent knowing every provider's path — is the map this whole layer exists to
    /// avoid.</remarks>
    Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance);

    /// <summary>Is it reachable, and does this key work? Never throws: a failure is the answer.</summary>
    Task<ProviderCheck> TestAsync(AiProviderInstance instance, CancellationToken ct = default);

    /// <summary>
    /// The models it serves, or an empty list when it cannot be asked.
    /// </summary>
    /// <remarks>The list can be long — <c>opencode models</c> returned 374 entries against OpenRouter —
    /// so what shows it is a searchable field and not a combo box.</remarks>
    Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default);
}
