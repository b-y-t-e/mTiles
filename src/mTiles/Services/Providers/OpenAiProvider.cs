using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>OpenAI's own API — the one service that serves both OpenAI flavors.</summary>
/// <remarks>Which is exactly why the flavor enum splits them: codex speaks
/// <see cref="ApiFlavor.OpenAiResponses"/> and everything else here speaks
/// <see cref="ApiFlavor.OpenAiChatCompletions"/>, and only this provider and OpenRouter serve both.
/// </remarks>
public sealed class OpenAiProvider : AiProvider
{
    public override string Id => "openai";
    public override string DisplayName => "OpenAI";

    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.OpenAiResponses];

    public override Uri? DefaultBaseUrl => new("https://api.openai.com/");

    /// <summary>Both shapes sit under <c>v1</c>, which is where an OpenAI-compatible client expects to
    /// be pointed — the version is part of the base address here, not of the path a client appends.
    /// </summary>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        ApiFlavors.Contains(flavor) && BaseUrlFor(instance) is { } baseUrl
            ? new Uri(baseUrl, "v1")
            : null;

    /// <inheritdoc cref="AnthropicProvider.TestAsync" />
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(instance, ct);
        return models.Count > 0
            ? ProviderCheck.Reached($"{models.Count} models")
            : ProviderCheck.Failed("OpenAI did not answer with a model list — check the key and the address.");
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "v1/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }
}
