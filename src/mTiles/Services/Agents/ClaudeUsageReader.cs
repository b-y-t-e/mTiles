using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// What a Claude Code subscription says is left of its two windows.
/// </summary>
/// <remarks>
/// <para><b>The endpoint is undocumented and is somebody else's</b> — it is the one the CLI's own
/// <c>/usage</c> command reads, measured on this machine on 2026-09-01 — so every field is read
/// defensively, absent means <c>null</c>, and a non-200 becomes a sentence on the card rather than an
/// exception anywhere. It can move without notice, and when it does the cost is one card that says so.
/// </para>
/// <para><b>It handles an OAuth token this application did not issue.</b> The token is read from the
/// CLI's own <c>.credentials.json</c> at the moment of the call, sent only to
/// <see cref="UsageEndpoint"/>, and never logged, persisted or shown. That is worth stating out loud
/// because it is the first thing here that carries somebody else's credential over the network.</para>
/// <para>Separate from <see cref="ClaudeAgent"/> because it is a different reason to change: the agent
/// knows where a login's files are, this knows what the usage service answers. The agent supplies the
/// token and this supplies the report.</para>
/// </remarks>
public static class ClaudeUsageReader
{
    /// <summary>Where the question goes. Absolute, because this is not a provider instance's address —
    /// a subscription is served by Anthropic and nowhere else.</summary>
    public static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");

    /// <summary>The beta the OAuth-authenticated endpoints are behind, measured 2026-09-01.</summary>
    private const string OauthBeta = "oauth-2025-04-20";

    /// <summary>How this reader's one call is made. Replaced in tests; null everywhere else.</summary>
    /// <remarks>A handler rather than a client, the same seam and for the same reason as
    /// <c>AiProvider.HandlerFactory</c>: without it no test of this layer could run without a live
    /// subscription.</remarks>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>How long the answer is waited for. A dashboard refresh must not hang on a slow
    /// network.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Asks the service, and answers a report either way.
    /// </summary>
    /// <remarks>Never null and never a throw: this is called for an account that exists, so silence has
    /// to arrive as a sentence in the place the figures would have been.</remarks>
    public static async Task<AiUsageReport> ReadAsync(string sourceId, string sourceName, string? plan,
        string accessToken, DateTimeOffset measuredAt, string? accountKey = null,
        CancellationToken ct = default)
    {
        var json = await FetchAsync(accessToken, ct);

        return json is null
            ? AiUsageReport.Failed(sourceId, sourceName,
                "Anthropic did not answer the usage question for this account.", measuredAt)
            : Parse(json, sourceId, sourceName, plan, measuredAt, accountKey);
    }

    /// <summary>The two windows the answer describes.</summary>
    /// <remarks><para>A window the document does not carry is left out rather than drawn as empty: a
    /// five-hour bar at 0% is what an account that has just reset and an account whose field was renamed
    /// both look like, and only one of those is worth believing.</para>
    /// <para>A document that carries neither window is a format that has moved, and it says so — that
    /// is the difference between this and answering a report with no bars in it, which reads on screen
    /// as an account with nothing to report.</para></remarks>
    public static AiUsageReport Parse(string json, string sourceId, string sourceName, string? plan,
        DateTimeOffset measuredAt, string? accountKey = null)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            return AiUsageReport.Failed(sourceId, sourceName,
                "Anthropic's answer was not readable, so the format has probably moved.", measuredAt);
        }

        using (document)
        {
            var windows = new[]
                {
                    Window(document.RootElement, "five_hour", "5h", TimeSpan.FromHours(5)),
                    Window(document.RootElement, "seven_day", "7d", TimeSpan.FromDays(7)),
                }
                .OfType<AiUsageWindow>()
                .ToArray();

            return windows.Length == 0
                ? AiUsageReport.Failed(sourceId, sourceName,
                    "Anthropic answered, but named no limit window this build recognises.", measuredAt)
                : new AiUsageReport(sourceId, sourceName, plan, windows, RemainingCredit: null,
                    Currency: null, measuredAt, Problem: null, accountKey);
        }
    }

    /// <summary>One window out of the document, or null when it does not carry it.</summary>
    private static AiUsageWindow? Window(JsonElement root, string field, string label, TimeSpan length)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(field, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var used)
            || used.ValueKind != JsonValueKind.Number)
            return null;

        return new AiUsageWindow(label, length, UsedPercent: used.GetDouble(),
            ResetsAt: UsageInstant.From(window, "resets_at"));
    }

    /// <summary>The document, or null for every way of not getting one.</summary>
    private static async Task<string?> FetchAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = HandlerFactory is { } factory
                ? new HttpClient(factory(), disposeHandler: true)
                : new HttpClient();
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", OauthBeta);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status and never the body: an error page from an endpoint authenticated with a
                // bearer token is not something to copy into a log file.
                Trace.TraceWarning("The Claude usage endpoint answered {0}.", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Asking Anthropic for usage failed: {0}", ex.Message);
            return null;
        }
    }
}
