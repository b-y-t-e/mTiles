using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// What codex last said about its own limits, out of the transcript it writes.
/// </summary>
/// <remarks>
/// <para><b>There is no endpoint.</b> Measured 2026-09-01: <c>backend-api/codex/usage</c> answers 403 at
/// the edge, so the only place a codex subscription states its windows is the <c>token_count</c> event
/// it records in its own rollout file. This reads the same files <see cref="SessionCapture"/> already
/// depends on and adds no new fragility — but it does inherit that one: the format is somebody else's,
/// and when it moves the card says so rather than showing a figure.</para>
/// <para><b>The figures are as fresh as the last reply, and the card has to say which.</b> That is why
/// the report's <c>MeasuredAt</c> is the event's own timestamp and not the moment of the read: a reading
/// older than the window it describes is a stale number, and shown as current it is worse than no number
/// at all.</para>
/// </remarks>
public static class CodexUsageReader
{
    /// <summary>
    /// The newest reading under a codex home, or a report saying why there is none.
    /// </summary>
    /// <param name="sessionsRoot">The <c>sessions</c> directory of the codex home being asked about —
    /// per sign-in, because <c>CODEX_HOME</c> relocates it and a second subscription is a second set of
    /// limits.</param>
    /// <remarks><b>Newest-first until one answers, not the newest one alone.</b> A session opened a
    /// minute ago has written its file and not yet had a reply, so it carries no reading at all — and
    /// asking only that file threw away the perfectly good figures the session before it recorded. The
    /// walk is bounded by <see cref="RolloutsExamined"/>: a sessions directory holds every conversation
    /// ever had here, and a machine with no codex reading anywhere must not cost a scan of all of
    /// them.</remarks>
    public static AiUsageReport Read(string sourceId, string sourceName, string sessionsRoot,
        DateTimeOffset now)
    {
        var rollouts = NewestRollouts(sessionsRoot);
        if (rollouts.Count == 0)
            return AiUsageReport.Failed(sourceId, sourceName,
                "codex has not written a session here yet, so it has said nothing about its limits.",
                now);

        foreach (var rollout in rollouts)
            if (NewestReading(rollout, sourceId, sourceName, now) is { } report)
                return report;

        return AiUsageReport.Failed(sourceId, sourceName,
            "codex's recent sessions report no limits — it states them only after a reply.", now);
    }

    /// <summary>How many rollouts back the walk goes before giving up.</summary>
    /// <remarks>Enough to see past a handful of sessions opened and abandoned without a reply, which is
    /// the case this exists for, and far short of a directory that holds years of conversations.</remarks>
    private const int RolloutsExamined = 8;

    /// <summary>
    /// One <c>token_count</c> line as a report, or null when it is not one.
    /// </summary>
    /// <remarks><b>The rate limits are searched for rather than walked to.</b> codex has moved this
    /// event's nesting once already — it has been under <c>payload</c> and directly on the line — and a
    /// hard path is a reader that answers null for a document that plainly contains the figures. What is
    /// fixed is the name of the object and the names inside it, which is as much of somebody else's
    /// format as it is safe to depend on.</remarks>
    public static AiUsageReport? Parse(string? line, string sourceId, string sourceName,
        DateTimeOffset fallbackMeasuredAt)
    {
        if (line is not { Length: > 0 }) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            if (Find(document.RootElement, "rate_limits") is not { } limits) return null;

            var measuredAt = UsageInstant.From(document.RootElement, "timestamp") ?? fallbackMeasuredAt;
            var windows = new[]
                {
                    Window(limits, "primary", measuredAt),
                    Window(limits, "secondary", measuredAt),
                }
                .OfType<AiUsageWindow>()
                .ToArray();

            return windows.Length == 0
                ? null
                : new AiUsageReport(sourceId, sourceName, Plan: null, windows,
                    RemainingCredit: Balance(document.RootElement), Currency: "$", measuredAt,
                    Problem: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One of the two limit windows, or null when it is not stated.</summary>
    /// <remarks>The label is the window's own length rather than codex's word for it: <c>primary</c> and
    /// <c>secondary</c> mean nothing on a card, and <c>window_minutes</c> says which is the five hours
    /// and which the week without this file having to assume the order.</remarks>
    private static AiUsageWindow? Window(JsonElement limits, string field, DateTimeOffset measuredAt)
    {
        if (!limits.TryGetProperty(field, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percent", out var used)
            || used.ValueKind != JsonValueKind.Number)
            return null;

        var length = Minutes(window, "window_minutes");
        return new AiUsageWindow(UsageWindowLabel.For(length), length, UsedPercent: used.GetDouble(),
            ResetsAt: UsageInstant.From(window, "resets_at", measuredAt)
                      ?? UsageInstant.From(window, "resets_in_seconds", measuredAt));
    }

    /// <summary>The window's length, or zero where it is not stated — which the pace then reads as
    /// unknown rather than guessing a week.</summary>
    private static TimeSpan Minutes(JsonElement window, string field) =>
        window.TryGetProperty(field, out var minutes)
        && minutes.ValueKind == JsonValueKind.Number
        && minutes.TryGetInt64(out var value) && value > 0
            ? TimeSpan.FromMinutes(value)
            : TimeSpan.Zero;

    /// <summary>What is left on the account where codex states credits, or null.</summary>
    private static decimal? Balance(JsonElement root) =>
        Find(root, "credits") is { } credits
        && credits.TryGetProperty("balance", out var balance)
        && balance.ValueKind == JsonValueKind.Number
            ? balance.GetDecimal()
            : null;

    /// <summary>The first object with that name anywhere in the document, or null.</summary>
    /// <remarks>Depth-first and bounded by the document's own shape: a rollout line is a handful of
    /// nested objects, not a tree of unknown depth.</remarks>
    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object)
            return found;

        foreach (var property in element.EnumerateObject())
            if (Find(property.Value, name) is { } nested)
                return nested;

        return null;
    }

    /// <summary>The newest rollout files under a sessions root, newest first.</summary>
    /// <remarks>Newest by last write and not by creation, which is the opposite of
    /// <see cref="SessionCapture.NewestSessionId"/> and deliberately: that one is looking for the
    /// session <em>this tile</em> started, while this one wants whichever session spoke most recently,
    /// however long ago it was opened.</remarks>
    private static IReadOnlyList<string> NewestRollouts(string sessionsRoot)
    {
        try
        {
            if (!Directory.Exists(sessionsRoot)) return [];

            return
            [
                .. new DirectoryInfo(sessionsRoot)
                    .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(RolloutsExamined)
                    .Select(file => file.FullName),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Reading codex's session directory failed: {0}", ex.Message);
            return [];
        }
    }

    /// <summary>The newest reading in one file, or null when it carries none.</summary>
    /// <remarks><para><b>The newest line that <em>parses</em>, not the newest line that mentions the
    /// name.</b> <c>rate_limits</c> is a substring, and a conversation about rate limits puts it in a
    /// message event — which then stood in for the reading, parsed to nothing, and had the card report
    /// no limits while a genuine <c>token_count</c> sat a few lines above it. Every candidate is
    /// therefore parsed and the last one that yields a report wins.</para>
    /// <para>The whole file is walked, one line at a time and only one report kept: a rollout runs to
    /// megabytes, and the cheap <c>Contains</c> is what keeps the parse off the thousands of lines that
    /// are the conversation itself.</para>
    /// <para>Opened with the sharing a live session needs: codex appends to this file for as long as the
    /// conversation lasts, and a reader that locked it would break the very session it is asking
    /// about.</para></remarks>
    private static AiUsageReport? NewestReading(string path, string sourceId, string sourceName,
        DateTimeOffset now)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            AiUsageReport? newest = null;
            while (reader.ReadLine() is { } line)
                if (line.Contains("rate_limits", StringComparison.Ordinal))
                    newest = Parse(line, sourceId, sourceName, now) ?? newest;

            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Reading a codex rollout failed: {0}", ex.Message);
            return null;
        }
    }
}
