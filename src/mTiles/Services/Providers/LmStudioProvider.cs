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
/// <para><b>A compatible endpoint is not a compatible model.</b> The Anthropic route is served by
/// LM Studio, but what answers on it is whatever chat template the loaded model ships, and the
/// conversion between the two is neither's contract. Measured 2026-08-31 against
/// <c>prism-ml/bonsai-27b</c>: simple calls, multi-turn conversations, multi-block system prompts,
/// tools and a <c>tool_use</c>/<c>tool_result</c> round trip all answered 200, while a real Claude Code
/// session failed with <c>500 … Jinja Exception: System message must be at the beginning</c> — raised
/// by the <em>model's</em> template, from a shape none of those probes reproduced. The same model also
/// returned <c>"content": []</c> after reasoning for the whole budget, and warned that it supports only
/// <c>on</c>/<c>off</c> where an effort level was asked for.
/// <para>So a pairing that tests green here can still fail on the tile, and the honest place to say so
/// is the agent's own output — which is what the user sees. This is the same caveat OpenRouter's
/// Anthropic-compatible endpoint carries, one layer further down: there it is the gateway that is not a
/// drop-in, here it is the model behind it.</para></para>
/// <para>No authentication of any kind, so a reachable instance is open to everyone on that network.
/// </para>
/// </remarks>
public sealed class LmStudioProvider : AiProvider, ILocalAiProvider
{
    public override string Id => "lmstudio";
    public override string DisplayName => "LM Studio";
    /// <summary>
    /// Both shapes, measured 2026-08-31 against the running server.
    /// </summary>
    /// <remarks><b>This said OpenAI-only, and that had stopped being true.</b> LM Studio now serves a
    /// real Anthropic Messages API at <c>/v1/messages</c>: asked for a completion it answers with a
    /// <c>msg_…</c> id, <c>"type": "message"</c>, a <c>content</c> array of typed blocks,
    /// <c>stop_reason</c> and <c>usage.input_tokens</c> — the shape itself, not a 200 from a catch-all
    /// route. While the list said otherwise, Claude Code and a local server were reported incompatible
    /// and the pairing was hidden from the chooser, which is the one place that could have said why.
    /// <para>Ollama is deliberately not given the same: its <c>/v1/messages</c> answers <b>404</b>,
    /// measured the same day. The flavors are what each server actually serves, one at a time.</para>
    /// </remarks>
    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.Anthropic];

    /// <summary>
    /// This machine, on the port LM Studio documents.
    /// </summary>
    /// <remarks><b>This used to be null</b>, on the reasoning that a local server has no address until
    /// somebody says where it is. True of a server on another machine and wrong about the case that
    /// actually happens: LM Studio runs on the machine in front of the user and documents
    /// <c>localhost:1234</c>, so an empty field meant no call was made at all — and the failure that
    /// came back was indistinguishable from an unreachable server, under a placeholder that had
    /// promised the field could be left blank. Empty now means what it means for every other provider:
    /// the service's own address.</remarks>
    public override Uri? DefaultBaseUrl => new($"http://localhost:{DefaultPort}/");

    public override int DefaultPort => 1234;

    /// <summary>
    /// Its documented default, and the other one that turns up.
    /// </summary>
    /// <remarks>Measured 2026-08-31 on a machine whose <c>http-server-config.json</c> said
    /// <c>"port": 8080</c> and whose server was serving a loaded model there. That file records the
    /// port in force and not whether anybody changed it, so this does not settle which number LM Studio
    /// ships — and that is the argument for probing both rather than for picking the winner. 1234 stays
    /// <see cref="DefaultPort"/> because a bare host has to resolve to something, and it is the one the
    /// documentation names.</remarks>
    public IReadOnlyList<int> DiscoveryPorts => [DefaultPort, 8080];
    public override bool NeedsApiKey => false;
    public override bool IsLocal => true;

    /// <summary>
    /// Two shapes, two addresses — and the version belongs to one of them and not the other.
    /// </summary>
    /// <remarks>An OpenAI-compatible client is pointed at <c>v1</c> and appends
    /// <c>/chat/completions</c>; Claude Code is pointed at an API <b>root</b> and appends
    /// <c>/v1/messages</c> itself. Handing both the same address is what sent Claude Code to
    /// <c>…/v1/v1/messages</c> against OpenRouter, and it would do the same here.
    /// <para>Its own <c>api/v0</c> route is for asking about the server, not for talking to a model, so
    /// it is not a flavor.</para></remarks>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        BaseUrlFor(instance) is not { } baseUrl
            ? null
            : flavor switch
            {
                ApiFlavor.OpenAiChatCompletions => new Uri(baseUrl, "v1"),
                ApiFlavor.Anthropic => baseUrl,
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
                $"Nothing answered as LM Studio at {Address(instance)}. Its server has to be "
                + "running on that port — LM Studio shows it as \"Reachable at\" — and reachable from "
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
    /// <remarks>The listing also carries <c>max_context_length</c> — the window the model was loaded
    /// with, which is the one answer to <see cref="ContextWindowAsync"/> that needs no second call.</remarks>
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
                    ContextWindowTokens = entry.TryGetProperty("max_context_length", out var window)
                                          && window.ValueKind == JsonValueKind.Number
                                          && window.TryGetInt64(out var tokens)
                        ? tokens
                        : null,
                });
        }
        return models;
    }
}
