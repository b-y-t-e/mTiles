using System.Net.Http;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>Anthropic's own API — what Claude Code talks to when nothing has been configured.</summary>
public sealed class AnthropicProvider : AiProvider
{
    public override string Id => "anthropic";
    public override string DisplayName => "Anthropic";
    public override IReadOnlyList<ApiFlavor> ApiFlavors => [ApiFlavor.Anthropic];
    public override Uri? DefaultBaseUrl => new("https://api.anthropic.com/");

    /// <summary>Its own header, not a bearer token, and a version it refuses to answer without.</summary>
    protected override void Authenticate(HttpRequestMessage request, AiProviderInstance instance)
    {
        if (instance.ApiKey.Length > 0)
            request.Headers.Add("x-api-key", instance.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    /// <summary>
    /// The model list is the check.
    /// </summary>
    /// <remarks>There is no per-key balance endpoint here, so the balance stays null — which means "this
    /// service does not say" and is the honest answer, where a zero would read as an exhausted key.
    /// </remarks>
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(instance, ct);
        return models.Count > 0
            ? ProviderCheck.Reached($"{models.Count} models")
            : ProviderCheck.Failed("Anthropic did not answer with a model list — check the key and the address.");
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "v1/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }
}
