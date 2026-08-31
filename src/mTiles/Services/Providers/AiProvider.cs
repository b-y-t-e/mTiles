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
    /// <remarks>Null unless the service says otherwise, which is the safe direction: a wrong variable
    /// name is a run authenticated as somebody else, while none at all is a run that fails and says
    /// so.</remarks>
    public virtual string? KeyEnvironmentVariable => null;

    /// <inheritdoc />
    /// <remarks>Our own id, because it was chosen to be the name these catalogues use — all four hosted
    /// ones checked against models.dev, which is opencode's own catalogue. An override is for a
    /// catalogue that spells one differently, and would be a measurement rather than a guess.</remarks>
    public virtual string CatalogueId => Id;

    /// <inheritdoc />
    public virtual bool IsLocal => false;

    /// <summary>
    /// How this provider's HTTP calls are made. Replaced in tests; null everywhere else.
    /// </summary>
    /// <remarks>Deliberately a handler and not a client: a client carries the base address and the
    /// timeout, which are the two things per instance that a test of this layer most wants to see
    /// actually applied.</remarks>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <inheritdoc />
    /// <remarks>The parse is done here rather than at the field, so the stored text stays what the user
    /// typed and a mistyped address is one failed call rather than a value silently rewritten.</remarks>
    public Uri? BaseUrlFor(AiProviderInstance instance) =>
        instance.BaseUrl.Trim().Length == 0
            ? DefaultBaseUrl
            // Typed and unparseable is not the same as not typed. Since the local providers gained a
            // default of their own, `Parse(...) ?? DefaultBaseUrl` quietly sent "192.168.1.10:abc" and
            // "ftp://box" to localhost — a Test that passes and a tile that runs against this machine
            // while the row shows another address. Null instead, which every caller already treats as
            // "there is nowhere to call".
            : ProviderEndpoint.Parse(instance.BaseUrl, DefaultPort);

    /// <summary>The address a call would go to, spelled for a message, or what is wrong with the one
    /// that was typed.</summary>
    /// <remarks><para>Since a typed address that cannot be parsed answers null rather than falling back
    /// to this machine, interpolating <see cref="BaseUrlFor"/> left a failure sentence reading
    /// "answered at ." — blank in the one case where the user has made a typo and it could be
    /// named.</para>
    /// <para>Here rather than on the two local providers that word such a sentence: it is one rule
    /// about how an instance's address reads, and two identical copies of it are two places for it to
    /// drift — the argument <c>SafePathComponent</c> was extracted on.</para></remarks>
    protected string Address(AiProviderInstance instance) =>
        BaseUrlFor(instance)?.ToString()
        ?? $"\"{instance.BaseUrl.Trim()}\", which is not an address this can read";

    /// <summary>
    /// The address for a flavor this provider serves — its own, unless a subclass serves that shape
    /// somewhere else.
    /// </summary>
    /// <remarks>Null for a flavor it does not serve, which is the same answer as "not compatible" and
    /// has to be, or an agent could be handed an address for a wire format nothing there speaks.
    /// </remarks>
    public virtual Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        ApiFlavors.Contains(flavor) ? BaseUrlFor(instance) : null;

    /// <summary>
    /// The answer a test owes before it makes no request at all, or null when there is an address.
    /// </summary>
    /// <remarks><b>Because the message blamed the key for a typo in the address.</b> An unreadable
    /// address makes <see cref="BaseUrlFor"/> answer null, every request answers null without being
    /// sent, and the four hosted providers then reported "did not accept this key" — so the first thing
    /// the user does is rotate a key that works. Said here rather than in four messages, for the reason
    /// <see cref="Address"/> stopped being written twice.</remarks>
    protected ProviderCheck? AddressProblem(AiProviderInstance instance) =>
        BaseUrlFor(instance) is null
            ? ProviderCheck.Failed($"Nothing was asked: {Address(instance)}. Correct it, or clear it "
                + $"to use {DisplayName}'s own address.")
            : null;

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

    /// <summary>
    /// Posts one JSON body and reads the JSON answer, or null when it could not be had.
    /// </summary>
    /// <remarks>The same rules as <see cref="GetJsonAsync"/>, which it mirrors: null rather than an
    /// exception, the detail traced. Here for the one provider whose per-model answer is a POST —
    /// Ollama's <c>api/show</c> — rather than a path on the shared client.</remarks>
    protected async Task<JsonDocument?> PostJsonAsync(AiProviderInstance instance, string path,
        string jsonBody, CancellationToken ct)
    {
        if (BaseUrlFor(instance) is not { } baseUrl)
            return null;

        try
        {
            using var client = ClientFor(instance);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path))
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
            };
            Authenticate(request, instance);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning("{0} answered {1} for POST {2}.", Id, (int)response.StatusCode, path);
                return null;
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), default, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Posting to {0} for {1} failed: {2}", Id, path, ex.Message);
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
                ContextWindowTokens = ContextWindowIn(entry),
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

    /// <summary>The context window a model entry carries, or null when it names none.</summary>
    /// <remarks>OpenRouter's <c>context_length</c> is the spelling the shared OpenAI-shaped reader is
    /// asked about. Read tolerantly: a field that moved or a value that is not a number is a provider
    /// that did not say, not a zero — a zero would become an environment variable and a compaction
    /// threshold of 0.8 tokens.</remarks>
    private static long? ContextWindowIn(JsonElement model) =>
        model.TryGetProperty("context_length", out var length)
        && length.ValueKind == JsonValueKind.Number
        && length.TryGetInt64(out var tokens)
            ? tokens
            : null;

    /// <inheritdoc />
    /// <remarks><b>The list is the answer for every provider whose listing carries the window.</b>
    /// Ollama is the exception, which is why this is virtual: its <c>api/tags</c> names models and
    /// says nothing else about them, and its window is on a per-model <c>api/show</c> call.</remarks>
    public virtual async Task<long?> ContextWindowAsync(AiProviderInstance instance, string model,
        CancellationToken ct = default)
    {
        if (model.Length == 0) return null;

        var models = await ModelsAsync(instance, ct);
        return models.FirstOrDefault(m =>
            string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))?.ContextWindowTokens;
    }
}
