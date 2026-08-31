using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// OpenRouter — one key in front of everybody else's models, and the only provider here that answers
/// what is left on it.
/// </summary>
/// <remarks>
/// <para>Three flavors, because it fronts all three: what actually works then depends on the model
/// chosen behind it, which is a thing this application cannot know and must not pretend to. Its
/// Anthropic-compatible endpoint in particular is <b>not a drop-in</b> for everything Claude Code does
/// — cache control, token counting and parts of tool streaming — so some combinations fail, and the
/// tile has to say which rather than reporting that "the AI tool reported a failure".</para>
/// </remarks>
public sealed class OpenRouterProvider : AiProvider
{
    public override string Id => "openrouter";
    public override string DisplayName => "OpenRouter";

    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.OpenAiResponses, ApiFlavor.Anthropic];

    public override Uri? DefaultBaseUrl => new("https://openrouter.ai/");

    /// <summary>The spelling every catalogue reads this service's key from.</summary>
    public override string? KeyEnvironmentVariable => "OPENROUTER_API_KEY";

    /// <summary>
    /// All three shapes are served under <c>api/v1</c> — but the version is part of the base address
    /// for one of them and part of the path for the other two, so the answer differs by flavor.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured 2026-08-31.</b> An OpenAI-shaped client is pointed at <c>api/v1</c> and appends
    /// <c>/chat/completions</c>; Claude Code is pointed at an API <em>root</em> and appends
    /// <c>/v1/messages</c> itself — the same convention as <c>https://api.anthropic.com</c>. One answer
    /// for all three therefore sent Claude Code to
    /// <c>https://openrouter.ai/api/v1/v1/messages</c>, which is a <b>404</b> where
    /// <c>https://openrouter.ai/api/v1/messages</c> is a 401 without a key.</para>
    /// <para>What made it expensive to find is what the 404 comes back as: Claude Code reports it as
    /// <i>"There's an issue with the selected model — it may not exist or you may not have access to
    /// it"</i>, so a correct model id on a working key reads as a model that is neither. This is the
    /// reason <see cref="AiProvider.EndpointFor"/> takes a flavor at all, and <c>ZaiProvider</c> is the
    /// same rule applied to a provider whose two paths were different enough to notice.</para>
    /// </remarks>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        BaseUrlFor(instance) is not { } baseUrl
            ? null
            : flavor switch
            {
                ApiFlavor.Anthropic => new Uri(baseUrl, "api"),
                ApiFlavor.OpenAiChatCompletions or ApiFlavor.OpenAiResponses =>
                    new Uri(baseUrl, "api/v1"),
                _ => null,
            };

    /// <summary>The key endpoint, which answers with the balance as well as with whether the key is a
    /// key at all.</summary>
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        if (AddressProblem(instance) is { } problem) return problem;

        using var document = await GetJsonAsync(instance, "api/v1/key", ct);
        if (document is null)
            return ProviderCheck.Failed("OpenRouter did not accept this key.");

        return ProviderCheck.Reached("key accepted", BalanceIn(document));
    }

    /// <summary>
    /// What is left on the key, or null where the answer does not carry it.
    /// </summary>
    /// <remarks>An unlimited key has no remaining figure at all, which is <c>null</c> and not zero — the
    /// distinction the whole tri-state exists for, since the two would otherwise read identically to a
    /// user whose key works perfectly well.</remarks>
    private static decimal? BalanceIn(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("limit_remaining", out var remaining)
            || remaining.ValueKind != JsonValueKind.Number)
            return null;

        return remaining.GetDecimal();
    }

    /// <summary>
    /// The full catalogue, with the one honest per-model effort answer any of these providers gives.
    /// </summary>
    /// <remarks>Long — 374 entries when this was written — which is why what shows it is a searchable
    /// field rather than a combo box.</remarks>
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "api/v1/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }
}
