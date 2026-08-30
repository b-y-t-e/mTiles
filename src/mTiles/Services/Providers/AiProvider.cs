using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// What every provider shares: where its calls go, how they are authenticated, and the rule that asking
/// a service a question never throws at the caller.
/// </summary>
/// <remarks>
/// <para><b>A failure is an answer here, not an exception.</b> Every one of these is a network call to
/// somebody else's service, and every caller is a piece of UI that has to say something either way — so
/// a refused connection comes back as <see cref="ProviderCheck.Failed"/> and a model list that could not
/// be fetched comes back empty. A test button that throws is a dialog with a stack trace in it.</para>
/// <para><see cref="HandlerFactory"/> is the seam the tests drive, in the same style as
/// <c>TerminalControl.PtyFactory</c>: without it every test of this layer would need a live key.</para>
/// </remarks>
public abstract class AiProvider : IAiProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<ApiFlavor> ApiFlavors { get; }
    public abstract Uri? DefaultBaseUrl { get; }

    /// <summary>Nothing to fill in, for a provider whose address carries its own port.</summary>
    public virtual int DefaultPort => 443;

    /// <summary>A key is the usual case; the local servers say otherwise.</summary>
    public virtual bool NeedsApiKey => true;

    /// <inheritdoc />
    public virtual bool IsLocal => false;

    /// <summary>
    /// How this provider's HTTP calls are made. Replaced in tests; null everywhere else.
    /// </summary>
    /// <remarks>Deliberately a handler and not a client: a client carries the base address and the
    /// timeout, which are the two things per instance that a test of this layer most wants to see
    /// actually applied.</remarks>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>Where this instance's calls go — its own address, or the provider's.</summary>
    /// <remarks>The parse is done here rather than at the field, so the stored text stays what the user
    /// typed and a mistyped address is one failed call rather than a value silently rewritten.</remarks>
    public Uri? BaseUrlFor(AiProviderInstance instance) =>
        ProviderEndpoint.Parse(instance.BaseUrl, DefaultPort) ?? DefaultBaseUrl;

    /// <summary>
    /// The address for a flavor this provider serves — its own, unless a subclass serves that shape
    /// somewhere else.
    /// </summary>
    /// <remarks>Null for a flavor it does not serve, which is the same answer as "not compatible" and
    /// has to be, or an agent could be handed an address for a wire format nothing there speaks.
    /// </remarks>
    public virtual Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        ApiFlavors.Contains(flavor) ? BaseUrlFor(instance) : null;

    /// <inheritdoc />
    public abstract Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default);

    /// <summary>The headers this provider authenticates with. Nothing, where it needs no key.</summary>
    protected virtual void Authenticate(HttpRequestMessage request, AiProviderInstance instance)
    {
        if (instance.ApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.ApiKey);
    }

    /// <summary>
    /// Fetches one JSON document from this instance, or null when it could not be fetched.
    /// </summary>
    /// <remarks><para>Null rather than an exception, and the reason is up in the class comment: every
    /// caller is UI. What the failure <em>was</em> is traced, because "could not reach the provider"
    /// with no detail anywhere is the shape of a support question nobody can answer.</para>
    /// <para><b>A timeout is a failure, not a cancellation.</b> <see cref="HttpClient.Timeout"/> is
    /// delivered as a <see cref="TaskCanceledException"/>, which <em>is</em> an
    /// <see cref="OperationCanceledException"/> — so excluding that type by itself let the one thing
    /// this catch exists for escape: an unreachable address typed into a provider row, thrown out of
    /// Test onto the UI thread with nobody above to catch it. The filter therefore asks
    /// <paramref name="ct"/> instead of the type: only a caller who really did cancel gets the throw
    /// they asked for.</para></remarks>
    protected async Task<JsonDocument?> GetJsonAsync(AiProviderInstance instance, string path,
        CancellationToken ct)
    {
        if (BaseUrlFor(instance) is not { } baseUrl)
            return null;

        try
        {
            using var client = ClientFor(instance);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
            Authenticate(request, instance);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning("{0} answered {1} for {2}.", Id, (int)response.StatusCode, path);
                return null;
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), default, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Asking {0} for {1} failed: {2}", Id, path, ex.Message);
            return null;
        }
    }

    /// <summary>A client for one call. Short-lived on purpose: these are a handful of user-triggered
    /// requests, not a hot path, and a pooled client would outlive the instance whose address and
    /// timeout it was built from.</summary>
    private HttpClient ClientFor(AiProviderInstance instance)
    {
        var client = HandlerFactory is { } factory
            ? new HttpClient(factory(), disposeHandler: true)
            : new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(instance.TimeoutSeconds);
        return client;
    }

    /// <summary>The models in an OpenAI-shaped <c>{"data":[{"id":…}]}</c> document — the reply four of
    /// these six providers give, which is what makes it worth sharing rather than repeating.</summary>
    protected static IReadOnlyList<AiModelInfo> ReadOpenAiModels(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<AiModelInfo>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || entry.TryGetProperty("id", out var id) is false
                || id.GetString() is not { Length: > 0 } modelId)
                continue;

            models.Add(new AiModelInfo
            {
                Id = modelId,
                DisplayName = entry.TryGetProperty("name", out var name)
                              && name.GetString() is { Length: > 0 } label
                    ? label
                    : modelId,
                SupportedEfforts = EffortsIn(entry),
            });
        }
        return models;
    }

    /// <summary>
    /// What OpenRouter's <c>supported_parameters</c> says about effort, or null for a provider that says
    /// nothing.
    /// </summary>
    /// <remarks><b>Null is "did not say" and never "none".</b> A missing list must not empty the effort
    /// combo — most providers never mention the subject, and the agent's own list is the real answer for
    /// all of them.</remarks>
    private static IReadOnlyList<AiEffort>? EffortsIn(JsonElement model)
    {
        if (!model.TryGetProperty("supported_parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
            return null;

        var mentionsReasoning = parameters.EnumerateArray()
            .Any(p => p.GetString() is "reasoning" or "reasoning_effort" or "include_reasoning");

        // A model that lists no reasoning parameter takes no effort at all — which is a real answer and
        // an empty list, as distinct from the null above.
        return mentionsReasoning
            ? [AiEffort.Low, AiEffort.Medium, AiEffort.High]
            : [];
    }
}
