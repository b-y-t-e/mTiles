using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// LM Studio's local server — an OpenAI-compatible endpoint on this machine or this network.
/// </summary>
/// <remarks>
/// <para><b>It will usually be found by being typed in, not by being discovered.</b> The server has to
/// have been started and "Serve on Local Network" enabled before anything but this machine can see it;
/// without that sentence in front of the scan, the feature reads as broken.</para>
/// <para>No authentication of any kind, so a reachable instance is open to everyone on that network.
/// </para>
/// </remarks>
public sealed class LmStudioProvider : AiProvider, ILocalAiProvider
{
    public override string Id => "lmstudio";
    public override string DisplayName => "LM Studio";
    public override IReadOnlyList<ApiFlavor> ApiFlavors => [ApiFlavor.OpenAiChatCompletions];

    /// <summary>None: a local server has no address until somebody says where it is.</summary>
    public override Uri? DefaultBaseUrl => null;

    public override int DefaultPort => 1234;
    public override bool NeedsApiKey => false;
    public override bool IsLocal => true;

    /// <summary>Its OpenAI-compatible endpoint is under <c>v1</c>; its own <c>api/v0</c> route is for
    /// asking about the server, not for talking to a model.</summary>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        flavor == ApiFlavor.OpenAiChatCompletions && BaseUrlFor(instance) is { } baseUrl
            ? new Uri(baseUrl, "v1")
            : null;

    /// <inheritdoc />
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(instance, ct);
        if (models.Count == 0)
            return ProviderCheck.Failed(
                "Nothing answered as LM Studio there. Its server has to be running, and reachable from "
                + "another machine only with \"Serve on Local Network\" enabled.");

        var loaded = models.Count(m => m.IsLoaded == true);
        return ProviderCheck.Reached($"{models.Count} models, {loaded} loaded");
    }

    /// <summary>
    /// The models it has, and which of them are loaded.
    /// </summary>
    /// <remarks>Its own <c>api/v0</c> route rather than the OpenAI-compatible one, for the single reason
    /// that only that route carries <c>state</c> — and which model is loaded right now is the whole of
    /// what <c>AiModelChoice.FirstLoaded</c> needs. The compatible route is the fallback for a build
    /// that does not serve <c>api/v0</c>, where every model's loaded state is simply unknown.</remarks>
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var detailed = await GetJsonAsync(instance, "api/v0/models", ct);
        if (detailed is not null)
            return ReadWithState(detailed);

        using var compatible = await GetJsonAsync(instance, "v1/models", ct);
        return compatible is null ? [] : ReadOpenAiModels(compatible);
    }

    /// <inheritdoc />
    public async Task<string?> FirstLoadedModelAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(instance, ct);
        return models.FirstOrDefault(m => m.IsLoaded == true)?.Id;
    }

    /// <inheritdoc />
    public async Task<bool> IsServingAsync(Uri baseUrl, CancellationToken ct = default)
    {
        // By protocol and not by port: an open 1234 is not proof of LM Studio, and a scan that said it
        // was would offer an instance that answers every call with somebody else's error page.
        var probe = new AiProviderInstance { ProviderId = Id, BaseUrl = baseUrl.ToString() };
        using var document = await GetJsonAsync(probe, "v1/models", ct);
        return document is not null;
    }

    /// <summary>LM Studio's own listing: the OpenAI shape plus a <c>state</c> of <c>loaded</c>.</summary>
    private static IReadOnlyList<AiModelInfo> ReadWithState(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<AiModelInfo>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out var id)
                && id.GetString() is { Length: > 0 } modelId)
                models.Add(new AiModelInfo
                {
                    Id = modelId,
                    DisplayName = modelId,
                    IsLoaded = entry.TryGetProperty("state", out var state)
                               && state.GetString() == "loaded",
                });
        }
        return models;
    }
}
