using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// What an Antigravity subscription says is left of its windows.
/// </summary>
/// <remarks>
/// <para><b>The endpoint is somebody else's and it is internal</b> — <c>v1internal</c>, the one agy's own
/// quota loop reads, measured on this machine on 2026-09-03 — so every field is read defensively, absent
/// means <c>null</c>, and a non-200 becomes a sentence on the card rather than an exception anywhere.
/// When it moves the cost is one card that says so.</para>
/// <para><b>The user agent is load-bearing and the reason is not obvious.</b> The service gates this
/// endpoint on the client's user agent carrying the word <c>antigravity</c>: with it the answer is 200,
/// and with anything else — the default one, or <c>agy/1.1.22</c>, which is what the binary is called —
/// it is <c>403 … You do not have a valid license of this product (#3501)</c>. Measured against a
/// consumer account that is perfectly well licensed, so that error names no real cause at all and would
/// have been read here as a subscription this machine does not have. It is spelled so that the request
/// still says who is making it.</para>
/// <para><b>Two groups of models, two windows each.</b> Gemini's models share one allowance and the
/// third-party ones (Claude, GPT) another, each with a five-hour and a weekly window — so an agy card
/// carries four windows where a Claude Code card carries two, which the tile's own layout already
/// handles by stacking them.</para>
/// <para>Separate from <see cref="AntigravityAgent"/> for the reason <see cref="ClaudeUsageReader"/> is
/// separate from its agent: the agent knows where the login is, this knows what the service answers.
/// </para>
/// </remarks>
public static class AntigravityUsageReader
{
    /// <summary>Where the question goes. Absolute, because a subscription is served by Google and
    /// nowhere else.</summary>
    public static readonly Uri UsageEndpoint =
        new("https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");

    /// <summary><b>Load-bearing.</b> The service reads the word <c>antigravity</c> in here as "this is
    /// the client that may ask"; without it the answer is a 403 about a licence the account has. The
    /// rest of it is this application naming itself, which the service does not mind.</summary>
    internal const string UserAgent = "antigravity mTiles (usage)";

    /// <summary>How this reader's one call is made. Replaced in tests; null everywhere else.</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>How long the answer is waited for. A dashboard refresh must not hang on a slow
    /// network.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Asks the service, and answers a report either way.
    /// </summary>
    /// <remarks>Never null and never a throw: this is called for an account that exists, so silence has
    /// to arrive as a sentence in the place the figures would have been.</remarks>
    public static async Task<AiUsageReport> ReadAsync(string sourceId, string sourceName,
        string accessToken, DateTimeOffset measuredAt, string? accountKey = null,
        CancellationToken ct = default)
    {
        var json = await FetchAsync(accessToken, ct);

        return json is null
            ? AiUsageReport.Failed(sourceId, sourceName,
                "Antigravity did not answer the usage question for this account.", measuredAt)
            : Parse(json, sourceId, sourceName, measuredAt, accountKey);
    }

    /// <summary>The windows the answer describes, in the order it lists them.</summary>
    /// <remarks>A document naming no window this build can read is a format that has moved, and it says
    /// so — that is the difference between this and a report with no bars in it, which reads on screen
    /// as an account with nothing to report.</remarks>
    public static AiUsageReport Parse(string json, string sourceId, string sourceName,
        DateTimeOffset measuredAt, string? accountKey = null)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            return AiUsageReport.Failed(sourceId, sourceName,
                "Antigravity's answer was not readable, so the format has probably moved.", measuredAt);
        }

        using (document)
        {
            var windows = Windows(document.RootElement).ToArray();

            return windows.Length == 0
                ? AiUsageReport.Failed(sourceId, sourceName,
                    "Antigravity answered, but named no limit window this build recognises.", measuredAt)
                : new AiUsageReport(sourceId, sourceName, Plan: null, windows, RemainingCredit: null,
                    Currency: null, measuredAt, Problem: null, accountKey);
        }
    }

    private static IEnumerable<AiUsageWindow> Windows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("buckets", out var buckets)
                || buckets.ValueKind != JsonValueKind.Array)
                continue;

            var name = GroupLabel(Text(group, "displayName"));

            foreach (var bucket in buckets.EnumerateArray())
                if (Window(bucket, name) is { } window)
                    yield return window;
        }
    }

    /// <summary>One bucket as a window, or null when it names no window this can read.</summary>
    /// <remarks><para><b>A bucket with no <c>remainingFraction</c> is read as nothing left, not as a
    /// bucket to skip.</b> This is proto3 over JSON, where a field at its default value is simply
    /// omitted — so an exhausted window is exactly the document that carries no fraction, and dropping
    /// it would take the card silent at the one moment it is worth reading. The cost of being wrong
    /// that way is a bar shown full when a field was renamed; the cost of the other way is a bar that
    /// disappears when the allowance runs out.</para>
    /// <para>A bucket that names no window at all <em>is</em> skipped: without one there is nothing to
    /// label it with and no length to measure the pace against.</para></remarks>
    private static AiUsageWindow? Window(JsonElement bucket, string? group)
    {
        if (bucket.ValueKind != JsonValueKind.Object
            || Text(bucket, "window") is not { Length: > 0 } window)
            return null;

        var remaining = bucket.TryGetProperty("remainingFraction", out var fraction)
            && fraction.ValueKind == JsonValueKind.Number
                ? Math.Clamp(fraction.GetDouble(), 0, 1)
                : 0;

        var (label, length) = Shape(window);

        return new AiUsageWindow(group is { Length: > 0 } ? $"{group} {label}" : label, length,
            UsedPercent: (1 - remaining) * 100,
            ResetsAt: UsageInstant.From(bucket, "resetTime"));
    }

    /// <summary>What the service's own name for a window is called here, and how long it lasts.</summary>
    /// <remarks>An unrecognised window keeps the service's own word and gets a length of
    /// <see cref="TimeSpan.Zero"/>, which is what <c>UsagePace</c> reads as "no pace to work out" — a
    /// window drawn without its tick rather than one measured against a length nobody stated.</remarks>
    private static (string Label, TimeSpan Length) Shape(string window) => window switch
    {
        "5h" => ("5h", TimeSpan.FromHours(5)),
        "daily" => ("1d", TimeSpan.FromDays(1)),
        "weekly" => ("7d", TimeSpan.FromDays(7)),
        "monthly" => ("30d", TimeSpan.FromDays(30)),
        _ => (window, TimeSpan.Zero),
    };

    /// <summary>The group's own name, with the word every one of them ends in taken off.</summary>
    /// <remarks>"Gemini Models" and "Claude and GPT models" both label every window under them, so the
    /// noun they share carries nothing and costs the width the figures need. What is left is the
    /// service's own words, which is the point — inventing a shorter name for somebody else's model
    /// family is how a card comes to say something the service never said.</remarks>
    private static string? GroupLabel(string? displayName)
    {
        if (displayName is not { Length: > 0 }) return null;

        var trimmed = displayName.Trim();

        return trimmed.EndsWith(" models", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^" models".Length]
            : trimmed;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The document, or null for every way of not getting one.</summary>
    private static async Task<string?> FetchAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = HandlerFactory is { } factory
                ? new HttpClient(factory(), disposeHandler: true)
                : new HttpClient();
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Post, UsageEndpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status and never the body: an error page from an endpoint authenticated with a
                // bearer token is not something to copy into a log file.
                Trace.TraceWarning("The Antigravity usage endpoint answered {0}.",
                    (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Asking Antigravity for usage failed: {0}", ex.Message);
            return null;
        }
    }
}
