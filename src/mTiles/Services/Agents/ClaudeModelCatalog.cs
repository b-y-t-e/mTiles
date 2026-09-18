using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace mTiles.Services.Agents;

/// <summary>
/// What Anthropic says about the models a subscription can reach — in particular how large a context
/// each one is served with.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> A tile on a subscription has no <c>AiProviderInstance</c>, so
/// there is no provider to ask the question every other configuration answers through
/// <c>ModelContextWindow.ContextOfAsync</c> — and that is the commonest configuration there is. Without
/// it the context bar counted tokens against nothing and drew figures with no bar.</para>
/// <para><b>Measured 2026-09-18.</b> <c>GET api.anthropic.com/v1/models</c> with Claude Code's own OAuth
/// token answers <c>200</c> and carries <c>max_input_tokens</c> per model: 1 000 000 for
/// <c>claude-opus-5</c>, <c>claude-sonnet-5</c>, <c>claude-fable-5-1</c> and the 4.6–4.8 families, and
/// 200 000 for <c>claude-opus-4-5-20251101</c> and <c>claude-haiku-4-5-20251001</c>. That measurement is
/// also what settled the question the other way: an earlier version of the bar assumed a flat 200 000
/// and drew a <em>full</em> bar over a 234k conversation on opus-5, whose window is five times that.
/// </para>
/// <para><b>The same request shape as <c>ClaudeUsageReader</c></b> — the OAuth beta header, the bearer
/// token out of <c>ClaudeCredentialStore</c> and nothing of the body in any log — but only a token that is
/// still good (<c>LiveAccessToken</c>). The usage card renews a stale one because nothing else would; this
/// is asked for a tile whose CLI is running on that login and renewing it itself, and spending the
/// rotating refresh token at the same moment as the CLI is how one of the two gets logged out. Read-only, and it is the user's own subscription being asked about
/// itself.</para>
/// <para>Cached for half an hour against the credentials file, the window <c>ModelContextWindow</c>
/// keeps and for the same reason: the answer moves when Anthropic ships a model, and the gauge asks
/// once per launch.</para>
/// </remarks>
public static class ClaudeModelCatalog
{
    /// <summary>Where the question goes. Absolute, because a subscription is served by Anthropic and
    /// nowhere else.</summary>
    public static readonly Uri ModelsEndpoint = new("https://api.anthropic.com/v1/models?limit=100");

    /// <summary>The beta the OAuth-authenticated endpoints are behind, measured 2026-09-01.</summary>
    private const string OauthBeta = "oauth-2025-04-20";

    /// <summary>
    /// The API version, which <c>v1/models</c> requires and the usage endpoint does not.
    /// </summary>
    /// <remarks>Measured 2026-09-18: without it the answer is <c>400 anthropic-version: header is
    /// required</c>, which is a perfectly clear message and arrives at a layer that turns every failure
    /// into "no window" — so the symptom was a bar that silently never appeared. The two endpoints being
    /// on the same host and the same token is exactly what made it easy to copy the request shape and
    /// leave this out.</remarks>
    private const string ApiVersion = "2023-06-01";

    /// <summary>How this reader's one call is made. Replaced in tests; null everywhere else.</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    private static readonly ConcurrentDictionary<string,
        (DateTimeOffset At, IReadOnlyDictionary<string, long> Windows)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, long> Empty =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The context this subscription is served for that model, or null when it will not say.
    /// </summary>
    /// <remarks>Never throws: what a failure costs is a bar, and a bar is not worth a tile. An id the
    /// catalogue does not carry is also null — a subscription pointed at a third-party model through a
    /// gateway is a configuration this endpoint knows nothing about, and guessing is what the whole of
    /// this class exists to stop.</remarks>
    public static async Task<long?> ContextWindowAsync(string credentialsFile, string model,
        CancellationToken ct = default)
    {
        if (model.Length == 0 || credentialsFile.Length == 0) return null;

        // The id can name its own window, and then it outranks the catalogue — see VariantWindow.
        if (VariantWindow(model) is { } named) return named;

        var windows = await WindowsAsync(credentialsFile, ct);
        return windows.TryGetValue(WithoutVariant(model), out var window) ? window : null;
    }

    /// <summary>
    /// The window a model id names in its own suffix, or null where it names none.
    /// </summary>
    /// <remarks>
    /// <para>Claude Code spells the long-context variants by putting the size in brackets after the
    /// model — <c>claude-sonnet-4-5[1m]</c> is that model served a million tokens — and that suffix
    /// travels all the way to the API as part of the model string, so it is what the transcript carries
    /// too. The catalogue lists the plain id, whose <c>max_input_tokens</c> is the <em>short</em>
    /// window: matched exactly the suffixed id found nothing, and falling through to the plain entry
    /// found 200 000 for a session really running on 1 000 000 — a bar drawn as full from a fifth of the
    /// way in, which reads as "about to run out" and is the one thing it must not say wrongly.</para>
    /// <para>Read rather than tabulated, so a variant nobody has met yet is right without a release:
    /// the suffix is a count and a scale, and a suffix in any other shape is simply not an answer —
    /// the plain id is then looked up as before.</para>
    /// </remarks>
    internal static long? VariantWindow(string model)
    {
        var opened = model.LastIndexOf('[');
        if (opened < 0 || !model.EndsWith("]", StringComparison.Ordinal)) return null;

        var variant = model[(opened + 1)..^1];
        if (variant.Length < 2) return null;

        var scale = char.ToLowerInvariant(variant[^1]) switch
        {
            'k' => 1_000L,
            'm' => 1_000_000L,
            _ => 0L,
        };

        return scale > 0 && long.TryParse(variant[..^1], out var count) && count > 0
            ? count * scale
            : null;
    }

    /// <summary>The model as the catalogue spells it, with any variant suffix taken off.</summary>
    private static string WithoutVariant(string model)
    {
        var opened = model.LastIndexOf('[');
        return opened > 0 && model.EndsWith("]", StringComparison.Ordinal) ? model[..opened] : model;
    }

    private static async Task<IReadOnlyDictionary<string, long>> WindowsAsync(string credentialsFile,
        CancellationToken ct)
    {
        if (Cache.TryGetValue(credentialsFile, out var held)
            && DateTimeOffset.UtcNow - held.At < Freshness)
            return held.Windows;

        // Never renewed from here: the CLI running on this login is what renews it — see
        // ClaudeCredentialStore.LiveAccessToken.
        if (ClaudeCredentialStore.LiveAccessToken(credentialsFile) is not { Length: > 0 } token) return Empty;

        var json = await FetchAsync(token, ct);
        if (json is null) return Empty;

        var windows = Parse(json);
        // A failed read is not cached: a network that came back must not leave the tile without a bar
        // for half an hour.
        if (windows.Count > 0) Cache[credentialsFile] = (DateTimeOffset.UtcNow, windows);
        return windows;
    }

    /// <summary>Every model the answer names, against its input window.</summary>
    /// <remarks><c>max_input_tokens</c> and not <c>max_tokens</c>: the second is how much the model may
    /// <em>write</em> in one reply (128 000 on the current families), which is a different number that
    /// happens to sit beside it and would draw every conversation as long past full.</remarks>
    internal static IReadOnlyDictionary<string, long> Parse(string json)
    {
        var windows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return windows;

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.GetString() is not { Length: > 0 } model
                    || !entry.TryGetProperty("max_input_tokens", out var size)
                    || !size.TryGetInt64(out var window)
                    || window <= 0)
                    continue;

                windows[model] = window;
            }
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning("Anthropic's model list did not parse: {0}", ex.Message);
        }

        return windows;
    }

    private static async Task<string?> FetchAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = HandlerFactory is { } factory
                ? new HttpClient(factory(), disposeHandler: true)
                : new HttpClient();
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", OauthBeta);
            request.Headers.Add("anthropic-version", ApiVersion);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status and never the body, the rule ClaudeUsageReader follows: an error page from
                // an endpoint authenticated with a bearer token is not something to copy into a log.
                Trace.TraceWarning("Anthropic's model list answered {0}.", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Asking Anthropic for its model list failed: {0}", ex.Message);
            return null;
        }
    }
}
