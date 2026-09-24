using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// How a usage endpoint's refusal is read and reported — one wording and one log line for every reader
/// that asks a service over HTTP.
/// </summary>
public static class UsageRefusal
{
    /// <summary>Logs a refusal and answers when the service asked to be asked again, if it did.</summary>
    /// <remarks>The status and never the body: an error page from an endpoint authenticated with a
    /// bearer token is not something to copy into a log file.</remarks>
    public static DateTimeOffset? Read(string service, HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = RetryAfter.From(response.StatusCode, response.Headers.RetryAfter, now);
        Trace.TraceWarning("The {0} usage endpoint answered {1}{2}.", service, (int)response.StatusCode,
            retry is { } until ? $"; it asked not to be asked before {until:HH:mm:ss}" : "");
        return retry;
    }

    /// <summary>The failure for an account whose service did not answer — naming the wait where it
    /// asked for one.</summary>
    public static AiUsageReport Failed(string sourceId, string sourceName, string service,
        DateTimeOffset measuredAt, DateTimeOffset? retryNotBefore) =>
        retryNotBefore is { } until
            ? AiUsageReport.Failed(sourceId, sourceName,
                $"{service} is rate-limiting usage questions for this account until {until.ToLocalTime():HH:mm}.",
                measuredAt, until)
            : AiUsageReport.Failed(sourceId, sourceName,
                $"{service} did not answer the usage question for this account.", measuredAt);
}
