using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services.Providers;

/// <summary>
/// The providers this application knows, and the one rule that says which of them an agent can be
/// pointed at.
/// </summary>
/// <remarks>One list — adding a provider is a class and a line here, the same argument that made
/// <see cref="AiAgentCatalog"/> and the tile registry lists rather than switches.</remarks>
public static class AiProviderCatalog
{
    /// <summary>Every provider, in the order a chooser should offer them.</summary>
    public static IReadOnlyList<IAiProvider> All { get; } =
    [
        new AnthropicProvider(),
        new CcsProvider(),
        new OpenAiProvider(),
        new OpenRouterProvider(),
        new ZaiProvider(),
        new LmStudioProvider(),
        new OllamaProvider(),
    ];

    /// <summary>The provider a stored id names, or null — a row naming a provider this build does not
    /// have finds nothing rather than failing the load.</summary>
    public static IAiProvider? Find(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : All.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The instance a stored id names, or null for the agent's own configuration — which is
    /// what an empty id means and what every seeded agent instance starts in.</summary>
    public static AiProviderInstance? FindInstance(AppSettings settings, string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId)
            ? null
            : settings.AiProviderInstances.FirstOrDefault(i => i.Id == instanceId);

    /// <summary>
    /// Whether this agent can be driven through this provider at all.
    /// </summary>
    /// <remarks>
    /// <b>The intersection of the two flavor lists, and nothing else.</b> Without the split inside
    /// OpenAI this would report codex and Ollama compatible — both "OpenAI" — and the launch would then
    /// fail, because codex speaks <see cref="ApiFlavor.OpenAiResponses"/> and Ollama serves
    /// <see cref="ApiFlavor.OpenAiChatCompletions"/>. A pairing offered and then failed is worse than
    /// one never offered.
    /// </remarks>
    public static bool IsCompatible(IAiAgent agent, IAiProvider provider) =>
        agent.ConsumesApiFlavors.Any(provider.ApiFlavors.Contains);

    /// <summary>The providers an agent can actually be pointed at.</summary>
    public static IReadOnlyList<IAiProvider> CompatibleWith(IAiAgent agent) =>
        [.. All.Where(provider => IsCompatible(agent, provider))];

    /// <summary>
    /// The effort levels left once the model has had its say — which, for nearly every provider, is all
    /// of them.
    /// </summary>
    /// <remarks>
    /// <para><b>"The provider did not say" must never become "no effort available".</b> A null
    /// <see cref="AiModelInfo.SupportedEfforts"/> is unknown, and unknown leaves the agent's own list
    /// exactly as it was: only OpenRouter answers this honestly, so treating silence as a denial would
    /// empty the effort combo for five providers out of six.</para>
    /// <para>An empty list <em>is</em> an answer — a model that takes no reasoning parameter at all —
    /// and it narrows to <see cref="AiEffort.ToolDefault"/> rather than to nothing, because a chooser
    /// with no options in it is a control the user cannot use to say anything.</para>
    /// </remarks>
    public static IReadOnlyList<AiEffort> NarrowEfforts(IReadOnlyList<AiEffort> agentEfforts,
        IReadOnlyList<AiEffort>? modelEfforts)
    {
        if (modelEfforts is null)
            return agentEfforts;

        var narrowed = agentEfforts
            .Where(effort => effort == AiEffort.ToolDefault || modelEfforts.Contains(effort))
            .ToList();

        return narrowed.Count > 0 ? narrowed : [AiEffort.ToolDefault];
    }
}
