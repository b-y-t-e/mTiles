using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// Ollama — its own API for what it has and what is loaded, and an OpenAI-compatible one for chatting.
/// </summary>
/// <remarks>
/// <para><b>It binds <c>127.0.0.1</c> unless <c>OLLAMA_HOST=0.0.0.0</c>,</b> so a network scan will
/// usually find nothing at all. Saying that is part of the feature.</para>
/// <para>Two flavors and neither of them <see cref="ApiFlavor.OpenAiResponses"/>: codex is therefore
/// <em>not</em> compatible with it through provider configuration, which is the pairing the flavor split
/// exists to keep off the screen. codex reaches local models by its own route
/// (<c>--oss --local-provider</c>) instead.</para>
/// <para>No authentication of any kind, so a reachable instance is open to everyone on that network.
/// </para>
/// </remarks>
public sealed class OllamaProvider : AiProvider, ILocalAiProvider
{
    public override string Id => "ollama";
    public override string DisplayName => "Ollama";

    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.OllamaNative];

    /// <summary>
    /// This machine, on the port Ollama binds by default.
    /// </summary>
    /// <remarks>See <c>LmStudioProvider.DefaultBaseUrl</c>: null here meant an empty address field made
    /// no call at all and reported it as a server that had not answered.</remarks>
    public override Uri? DefaultBaseUrl => new($"http://localhost:{DefaultPort}/");

    public override int DefaultPort => 11434;

    /// <inheritdoc />
    /// <remarks>The one port it is ever on: unlike LM Studio's, Ollama's is moved by
    /// <c>OLLAMA_HOST</c>, and a machine that has moved it is one this scan will not find by
    /// guessing.</remarks>
    public IReadOnlyList<int> DiscoveryPorts => [DefaultPort];
    public override bool NeedsApiKey => false;
    public override bool IsLocal => true;

    /// <summary>Chat goes to the OpenAI-compatible route under <c>v1</c>; its own flavor stays at the
    /// root, which is where <c>api/tags</c> and <c>api/ps</c> live.</summary>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        BaseUrlFor(instance) is not { } baseUrl
            ? null
            : flavor switch
            {
                ApiFlavor.OpenAiChatCompletions => new Uri(baseUrl, "v1"),
                ApiFlavor.OllamaNative => baseUrl,
                _ => null,
            };

    /// <inheritdoc />
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        // Named before anything is said about the server: an address that cannot be read is not a
        // server that did not answer.
        if (AddressProblem(instance) is { } problem) return problem;

        var models = await ModelsAsync(instance, ct);
        if (models.Count == 0)
            return ProviderCheck.Failed(
                $"Nothing answered as Ollama at {Address(instance)}. It binds 127.0.0.1 unless "
                + "OLLAMA_HOST=0.0.0.0, so another machine cannot reach it by default.");

        var loaded = await FirstLoadedModelAsync(instance, ct);
        return ProviderCheck.Reached(loaded is null
            ? $"{models.Count} models, none loaded"
            : $"{models.Count} models, {loaded} loaded");
    }

    /// <summary>What it has pulled. Its own <c>api/tags</c>, which is the route that actually answers on
    /// every build.</summary>
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "api/tags", ct);
        return document is null ? [] : ReadNames(document, "models");
    }

    /// <summary>What it has in memory right now — a different question from what it has pulled, and the
    /// one the "first loaded" sentinel is asking.</summary>
    public async Task<string?> FirstLoadedModelAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "api/ps", ct);
        return document is null ? null : ReadNames(document, "models").FirstOrDefault()?.Id;
    }

    /// <inheritdoc />
    public async Task<bool> IsServingAsync(Uri baseUrl, CancellationToken ct = default)
    {
        var probe = new AiProviderInstance { ProviderId = Id, BaseUrl = baseUrl.ToString() };
        using var document = await GetJsonAsync(probe, "api/tags", ct);
        return document is not null;
    }

    /// <summary>Ollama's shape: a named array of objects each carrying a <c>name</c>.</summary>
    private static IReadOnlyList<AiModelInfo> ReadNames(JsonDocument document, string arrayName)
    {
        if (!document.RootElement.TryGetProperty(arrayName, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return [];

        return [.. array.EnumerateArray()
            .Select(entry => entry.TryGetProperty("name", out var name) ? name.GetString() : null)
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Select(name => new AiModelInfo { Id = name, DisplayName = name })];
    }
}
