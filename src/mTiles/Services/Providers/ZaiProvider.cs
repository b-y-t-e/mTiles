using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// z.ai — an OpenAI-compatible API and, at <c>/api/anthropic</c>, an Anthropic-compatible one.
/// </summary>
/// <remarks>The second of those is the better-trodden path for driving Claude Code through somebody
/// other than Anthropic, which is the whole reason this provider is in the list rather than left to the
/// generic OpenAI-compatible route.</remarks>
public sealed class ZaiProvider : AiProvider
{
    public override string Id => "zai";
    public override string DisplayName => "z.ai";

    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.Anthropic];

    public override Uri? DefaultBaseUrl => new("https://api.z.ai/api/");

    /// <summary>
    /// Its two shapes live at two paths, which is the whole reason an endpoint is asked for per flavor.
    /// </summary>
    /// <remarks>An agent handed the OpenAI address for an Anthropic conversation fails with a 404 from
    /// somebody else's service, which names neither the setting nor the mistake.</remarks>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        BaseUrlFor(instance) is not { } baseUrl
            ? null
            : flavor switch
            {
                ApiFlavor.Anthropic => new Uri(baseUrl, "anthropic"),
                ApiFlavor.OpenAiChatCompletions => new Uri(baseUrl, "paas/v4"),
                _ => null,
            };

    /// <inheritdoc cref="AnthropicProvider.TestAsync" />
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(instance, ct);
        return models.Count > 0
            ? ProviderCheck.Reached($"{models.Count} models")
            : ProviderCheck.Failed("z.ai did not answer with a model list — check the key and the address.");
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "paas/v4/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }
}
